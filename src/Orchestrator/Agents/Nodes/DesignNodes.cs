using System.Globalization;
using System.Text;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;

namespace Orchestrator.Agents.Nodes;

/// <summary>Brownfield codebase reasoning: the card's module map narrows which files are read before the LLM sees any code.</summary>
public sealed class ImpactNode(AgentServices s) : INodeHandler
{
    private const int MaxContextChars = 14_000;

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);
        var card = await s.Projects.GetAsync(workspace.Slug, ct);
        var projectDir = s.ProjectDir(run);

        // Rank source files by overlap with the spec's vocabulary (cheap, deterministic), then include the best ones verbatim.
        var terms = Tokenizer.Terms(JsonText.Serialize(spec.FunctionalRequirements) + " " + spec.Title);
        var ranked = workspace.ExistingFeatureFiles
            .Where(f => f.StartsWith("src/"))
            .Select(f => (Path: f, Text: ProjectFiles.Read(projectDir, f) ?? ""))
            .Select(f => (f.Path, f.Text, Score: Tokenizer.Terms(f.Path + " " + f.Text).Intersect(terms).Count()))
            .OrderByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var context = new StringBuilder()
            .AppendLine(Prompts.Section("SPECIFICATION", JsonText.Serialize(new { spec.Title, spec.Summary, spec.FunctionalRequirements })))
            .AppendLine(Prompts.Section("MODULE MAP", JsonText.Serialize(card?.ModuleMap ?? [])))
            .AppendLine("### EXISTING CODE");
        foreach (var f in ranked)
        {
            if (context.Length + f.Text.Length > MaxContextChars) { context.AppendLine($"// FILE: {f.Path} (omitted for length)"); continue; }
            context.AppendLine($"// FILE: {f.Path}").AppendLine(f.Text);
        }

        var impact = await s.AskJsonAsync<Impact>(ctx, "impact", "impact analyst", Prompts.Impact, context.ToString(), ct, 1500);
        var result = NodeResult.Ok($"Impact: {impact.Files.Count} files, data model change: {impact.DataModelChange}. {impact.Summary}");
        foreach (var f in impact.Files) result.Because($"Impacted {f.Path} [{f.Risk}]: {f.Change}");
        foreach (var r in impact.Risks) result.Because($"Risk: {r}");
        return result.With(ArtifactKeys.Impact, JsonText.Serialize(impact));
    }
}

public sealed class DesignNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var nfr = AgentServices.Read<NfrArtifact>(run, ArtifactKeys.Nfr);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);
        var impact = AgentServices.TryRead<Impact>(run, ArtifactKeys.Impact);
        var profile = s.Profiles.Get(nfr.Profile);

        var context = new StringBuilder()
            .AppendLine(Prompts.Section("SPECIFICATION", JsonText.Serialize(spec)))
            .AppendLine(Prompts.Section("NON-FUNCTIONAL CONTEXT",
                string.Create(CultureInfo.InvariantCulture, $"Deployment profile: {profile.Name}. Expected {nfr.RequestsPerDay:N0} requests/day, peak {nfr.PeakRps:N0} RPS, availability {nfr.AvailabilityTarget}%. ") +
                $"Capacity verdict: {nfr.Verdict}. Profile strategies: {string.Join("; ", profile.Strategies)}"))
            .AppendLine(Prompts.Section("PLATFORM KIT", s.PlatformDoc))
            .AppendLine(Prompts.Section("PATTERN CATALOG", s.PatternCatalog));

        if (workspace.Mode == "brownfield")
        {
            var projectDir = s.ProjectDir(run);
            context.AppendLine(Prompts.Section("IMPACT ANALYSIS", JsonText.Serialize(impact)));
            var entityFiles = workspace.ExistingFeatureFiles.Where(f => f.StartsWith("src/") && (ProjectFiles.Read(projectDir, f) ?? "").Contains("IEntityTypeConfiguration"));
            foreach (var f in entityFiles) context.AppendLine($"// EXISTING ENTITY FILE: {f}").AppendLine(ProjectFiles.Read(projectDir, f));
        }

        var design = await s.AskJsonAsync<Design>(ctx, "architect", "software architect", Prompts.Architect, context.ToString(), ct, 3500);
        var result = NodeResult.Ok($"Design: {design.Entities.Count} entities, {design.Endpoints.Count} endpoints, {design.NfrPatterns.Count} NFR patterns, schema change: {design.SchemaChange}.");
        if (design.SchemaChange && workspace.Mode == "brownfield") result.RequestedActions.Add("schema.change");
        foreach (var d in design.Decisions) result.Because($"ADR: {d.Title} — {d.Decision} (because {d.Rationale})");
        return result.With(ArtifactKeys.Design, JsonText.Serialize(design));
    }

    public static readonly IGate Gate = Orchestrator.Core.Engine.Gate.Sync("design-completeness", (_, r) =>
    {
        if (r?.Artifacts.GetValueOrDefault(ArtifactKeys.Design) is not { } json) return GateResult.Fail("design missing");
        var d = JsonText.Deserialize<Design>(json);
        var v = new List<string>();
        if (d.Endpoints.Count == 0) v.Add("design must define at least one endpoint");
        if (d.NfrPatterns.Count == 0) v.Add("every non-functional concern must map to a pattern (nfrPatterns is empty)");
        v.AddRange(d.NfrPatterns.Where(p => string.IsNullOrWhiteSpace(p.Pattern) || string.IsNullOrWhiteSpace(p.Test))
            .Select(p => $"nfrPattern '{p.Nfr}' needs both a pattern and a test"));
        if (d.Decisions.Count == 0) v.Add("record at least one design decision with rationale");
        return GateResult.From(v);
    });
}

/// <summary>Decomposes the design into file tasks with dependencies; the engine expands them into graph nodes.</summary>
public sealed class PlanNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var design = run.Require(ArtifactKeys.Design);
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);
        var context = new StringBuilder()
            .AppendLine(Prompts.Section("REQUIREMENTS", string.Join("\n", spec.FunctionalRequirements.Select(f => $"{f.Id}: {f.Description}"))))
            .AppendLine(Prompts.Section("DESIGN", design))
            .AppendLine(Prompts.Section("EXISTING FILES", workspace.ExistingFeatureFiles.Count == 0
                ? "src/App/Features/FeatureRegistration.cs (template stub)"
                : string.Join("\n", workspace.ExistingFeatureFiles)));
        if (run.Get(ArtifactKeys.Impact) is { } impact) context.AppendLine(Prompts.Section("IMPACT ANALYSIS", impact));

        var plan = await s.AskJsonAsync<Plan>(ctx, "planner", "technical lead", Prompts.Planner, context.ToString(), ct, 2500);
        var result = NodeResult.Ok($"Plan: {plan.Tasks.Count(t => t.Kind == "code")} code + {plan.Tasks.Count(t => t.Kind == "test")} test tasks.");
        foreach (var t in plan.Tasks) result.Because($"{t.Id} {t.Action} {t.Path} (after {string.Join(",", t.DependsOn)}): {t.Description}");
        return result.With(ArtifactKeys.Plan, JsonText.Serialize(plan));
    }

    public static string NodeId(PlanTask t) => $"{(t.Kind == "test" ? "test" : "code")}:{t.Id}";

    public static IGate Gate(AgentServices s) => Orchestrator.Core.Engine.Gate.Sync("plan-validity", (ctx, r) =>
    {
        if (r?.Artifacts.GetValueOrDefault(ArtifactKeys.Plan) is not { } json) return GateResult.Fail("plan missing");
        var plan = JsonText.Deserialize<Plan>(json);
        var workspace = AgentServices.Read<WorkspaceArtifact>(ctx.Run, ArtifactKeys.Workspace);
        var existing = workspace.ExistingFeatureFiles.Append("src/App/Features/FeatureRegistration.cs").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var v = new List<string>();

        if (!plan.Tasks.Any(t => t.Kind == "code")) v.Add("plan needs at least one code task");
        if (!plan.Tasks.Any(t => t.Kind == "test")) v.Add("plan needs at least one test task (every change must be tested)");
        if (plan.Tasks.Count > s.Policy.Document.Limits.MaxFilesPerRun) v.Add($"plan exceeds {s.Policy.Document.Limits.MaxFilesPerRun} files (change-control limit)");
        if (plan.Tasks.Select(t => t.Id).Distinct().Count() != plan.Tasks.Count) v.Add("task ids must be unique");
        if (plan.Tasks.Select(t => t.Path.ToLowerInvariant()).Distinct().Count() != plan.Tasks.Count) v.Add("each file may appear in only one task");
        if (workspace.Mode == "greenfield" && !plan.Tasks.Any(t => t.Path.EndsWith("Features/FeatureRegistration.cs")))
            v.Add("src/App/Features/FeatureRegistration.cs must be modified to register the new feature");

        var ids = plan.Tasks.Select(t => t.Id).ToHashSet();
        foreach (var t in plan.Tasks)
        {
            var codeRoot = t.Path.StartsWith("src/App/Features/");
            var testRoot = t.Path.StartsWith("tests/App.Tests/Features/");
            if (!t.Path.EndsWith(".cs") || (t.Kind == "code" && !codeRoot) || (t.Kind == "test" && !testRoot))
                v.Add($"{t.Id}: path '{t.Path}' must be under src/App/Features/ (code) or tests/App.Tests/Features/ (test)");
            if (t.Action == "modify" && !existing.Contains(t.Path)) v.Add($"{t.Id}: cannot modify '{t.Path}' — it does not exist");
            v.AddRange(t.DependsOn.Where(d => !ids.Contains(d)).Select(d => $"{t.Id}: unknown dependency '{d}'"));
            v.AddRange(s.Policy.CheckFileWrite(t.Path, "").Select(p => $"{t.Id}: {p}"));
        }

        // Dependencies must form a DAG (validated with the same graph code the engine uses).
        try
        {
            var g = new WorkflowGraph();
            foreach (var t in plan.Tasks)
                g.Add(new NodeDefinition { Id = t.Id, Stage = "x", Handler = new DelegateHandler((_, _) => Task.FromResult(NodeResult.Ok())), DependsOn = t.DependsOn.Where(ids.Contains).ToList() });
            g.Validate();
        }
        catch (InvalidOperationException ex) { v.Add(ex.Message); }

        return GateResult.From(v);
    });
}

/// <summary>Writes one file. Sees the design, the platform kit and the files it depends on; validated by policy + conventions.</summary>
public sealed class CoderNode(AgentServices s, PlanTask task) : INodeHandler
{
    private const int MaxDependencyChars = 12_000;

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var plan = AgentServices.Read<Plan>(run, ArtifactKeys.Plan);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);
        var projectDir = s.ProjectDir(run);

        var context = new StringBuilder()
            .AppendLine(Prompts.Section("TARGET FILE", $"{task.Path} ({task.Action})\nTask {task.Id}: {task.Description}"))
            .AppendLine(Prompts.Section("REQUIREMENTS", string.Join("\n", spec.FunctionalRequirements.Select(f => $"{f.Id}: {f.Description} — {f.Acceptance}"))))
            .AppendLine(Prompts.Section("DESIGN", run.Require(ArtifactKeys.Design)))
            .AppendLine(Prompts.Section("PLATFORM KIT", s.PlatformDoc));

        var deps = new StringBuilder();
        foreach (var dep in plan.Tasks.Where(t => task.DependsOn.Contains(t.Id)))
        {
            var content = run.Get(ArtifactKeys.File(dep.Path)) ?? ProjectFiles.Read(projectDir, dep.Path);
            if (content is null || deps.Length + content.Length > MaxDependencyChars) continue;
            deps.AppendLine($"// FILE: {dep.Path}").AppendLine(content);
        }
        if (deps.Length > 0) context.AppendLine(Prompts.Section("DEPENDENCY FILES", deps.ToString()));

        if (task.Action == "modify")
        {
            // Always the pre-run version, so retries and replays see a stable base.
            var original = workspace.Checkpoint.Length > 0
                ? ProjectFiles.Read(Path.Combine(projectDir, ".versions", workspace.Checkpoint), task.Path)
                : null;
            original ??= ProjectFiles.Read(Path.Combine(s.Paths.TemplateDir), task.Path) ?? ProjectFiles.Read(projectDir, task.Path);
            if (original is not null) context.AppendLine(Prompts.Section("CURRENT CONTENT", original));
        }

        var agent = task.Kind == "test" ? "tester" : "coder";
        var output = await s.AskTextAsync(ctx, agent, task.Kind == "test" ? "test engineer" : "software engineer", Prompts.Coder, context.ToString(), ct, 4000);
        var code = AgentServices.ExtractCode(output);
        return NodeResult.Ok($"{task.Action} {task.Path} ({code.Split('\n').Length} lines)")
            .With(ArtifactKeys.File(task.Path), code)
            .Because($"{task.Id}: {task.Action} {task.Path}");
    }

    public static IGate Gate(AgentServices s, PlanTask task) => Orchestrator.Core.Engine.Gate.Sync("code-policy", (_, r) =>
    {
        var code = r?.Artifacts.GetValueOrDefault(ArtifactKeys.File(task.Path));
        if (string.IsNullOrWhiteSpace(code)) return GateResult.Fail("no code produced");
        var v = new List<string>();
        v.AddRange(s.Policy.CheckFileWrite(task.Path, code).Select(p => p.ToString()));
        v.AddRange(s.Policy.CheckConventions(task.Path, code).Select(p => p.ToString()));
        if (!code.Contains("namespace ")) v.Add("file must declare a namespace");
        if (code.Count(c => c == '{') != code.Count(c => c == '}')) v.Add("unbalanced braces — file looks truncated");
        if (task.Kind == "test" && !code.Contains("[Fact]") && !code.Contains("[Theory]")) v.Add("test file contains no [Fact] or [Theory]");
        return GateResult.From(v);
    });

    public static Func<RunContext, NodeResult, CancellationToken, Task> Commit(AgentServices s, PlanTask task) =>
        (run, result, ct) => ProjectFiles.WriteAsync(s.ProjectDir(run), task.Path, result.Artifacts[ArtifactKeys.File(task.Path)], ct);
}
