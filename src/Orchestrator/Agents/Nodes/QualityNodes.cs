using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Projects;

namespace Orchestrator.Agents.Nodes;

/// <summary>
/// Synchronisation point after the parallel implementation/test/docs branches. Objective checks only:
/// policy + conventions, template integrity, dependency allowlist, build, tests, coverage.
/// Failures are attributed to the responsible task nodes and sent back upstream (loop-back) with the exact errors.
/// </summary>
public sealed partial class ValidateNode(AgentServices s) : INodeHandler
{
    private static readonly string[] LockedTemplateFiles =
    [
        "src/App/Program.cs", "src/App/App.csproj", "src/App/appsettings.json", "tests/App.Tests/App.Tests.csproj",
        "tests/App.Tests/Support/TestApp.cs", "tests/App.Tests/Platform/PlatformTests.cs",
    ];

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var projectDir = s.ProjectDir(run);
        var plan = AgentServices.Read<Plan>(run, ArtifactKeys.Plan);
        var nodeByPath = plan.Tasks.ToDictionary(t => t.Path, PlanNode.NodeId, StringComparer.OrdinalIgnoreCase);
        var codeNodes = plan.Tasks.Where(t => t.Kind == "code").Select(PlanNode.NodeId).ToList();
        var testNodes = plan.Tasks.Where(t => t.Kind == "test").Select(PlanNode.NodeId).ToList();
        var retry = new Dictionary<string, List<string>>();
        var report = new StringBuilder("# Validation report\n\n");
        var hardFailures = new List<string>();

        void Target(string nodeId, string message)
        {
            if (!retry.TryGetValue(nodeId, out var list)) retry[nodeId] = list = [];
            if (list.Count < 20) list.Add(message);
        }

        // 1. Policy + conventions over all feature files.
        var policyViolations = 0;
        foreach (var file in ProjectFiles.FeatureFiles(projectDir))
        {
            var content = ProjectFiles.Read(projectDir, file) ?? "";
            foreach (var v in s.Policy.CheckConventions(file, content).Concat(s.Policy.ScanSecrets(file, content)))
            {
                policyViolations++;
                if (nodeByPath.TryGetValue(file, out var node)) Target(node, v.ToString());
                else hardFailures.Add(v.ToString());
            }
        }
        report.AppendLine($"- Policy & conventions: {(policyViolations == 0 ? "✅ pass" : $"❌ {policyViolations} violation(s)")}");

        // 2. Template integrity: platform files must be byte-identical to the template.
        var tampered = LockedTemplateFiles.Concat(Directory.EnumerateFiles(Path.Combine(s.Paths.TemplateDir, "src/App/Platform"), "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(s.Paths.TemplateDir, f).Replace('\\', '/')))
            .Where(f => ProjectFiles.Read(s.Paths.TemplateDir, f) != ProjectFiles.Read(projectDir, f))
            .ToList();
        hardFailures.AddRange(tampered.Select(f => $"[template-integrity] {f} differs from the platform template"));
        report.AppendLine($"- Template integrity: {(tampered.Count == 0 ? "✅ intact" : $"❌ {string.Join(", ", tampered)}")}");

        // 3. Dependency allowlist.
        foreach (var csproj in Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.AllDirectories).Where(f => !f.Contains(".versions")))
        {
            var ids = PackageRef().Matches(File.ReadAllText(csproj)).Select(m => m.Groups[1].Value);
            hardFailures.AddRange(s.Policy.CheckPackages(Path.GetRelativePath(projectDir, csproj), ids).Select(v => v.ToString()));
        }

        if (hardFailures.Count > 0 && retry.Count == 0)
            return NodeResult.Fail("Validation blocked by policy: " + string.Join("; ", hardFailures.Take(5)));

        // 4. Build.
        var build = await s.Processes.RunAsync("dotnet", "build App.slnx -nologo -v q -clp:NoSummary", projectDir, TimeSpan.FromMinutes(10), ct);
        var buildErrors = BuildError().Matches(build.Combined)
            .Select(m => (File: Relative(projectDir, m.Groups["file"].Value), Message: $"{m.Groups["code"].Value} line {m.Groups["line"].Value}: {m.Groups["msg"].Value.Trim()}"))
            .Distinct().ToList();
        if (build.ExitCode != 0)
        {
            report.AppendLine($"- Build: ❌ {buildErrors.Count} error(s)");
            foreach (var e in buildErrors)
            {
                if (nodeByPath.TryGetValue(e.File, out var node)) Target(node, e.Message);
                else foreach (var n in codeNodes) Target(n, $"{e.File}: {e.Message}");
            }
            if (retry.Count == 0) return NodeResult.Fail("Build failed without attributable errors: " + Tail(build.Combined));
            return LoopBack(retry, report, $"Build failed with {buildErrors.Count} error(s)");
        }
        report.AppendLine("- Build: ✅ succeeded");

        // 5. Tests + coverage.
        var resultsDir = Path.Combine(projectDir, "TestResults");
        if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, recursive: true);
        var test = await s.Processes.RunAsync("dotnet",
            $"test --no-build --coverlet --coverlet-output-format cobertura --results-directory \"{resultsDir}\"", projectDir, TimeSpan.FromMinutes(10), ct);
        var total = Int(TotalRegex().Match(test.Combined));
        var failed = Int(FailedCountRegex().Match(test.Combined));
        var failedTests = FailedTestRegex().Matches(test.Combined).Select(m => m.Groups["name"].Value).Distinct().ToList();
        var coverage = ReadCoverage(resultsDir);
        var minCoverage = s.Policy.Document.Quality.MinLineCoverage;

        report.AppendLine($"- Tests: {(test.ExitCode == 0 ? "✅" : "❌")} {total - failed}/{total} passed");
        report.AppendLine($"- Line coverage: {(coverage >= minCoverage ? "✅" : "❌")} {coverage:0.0}% (policy minimum {minCoverage}%)");
        if (failedTests.Count > 0) report.AppendLine("\n## Failed tests\n" + string.Join("\n", failedTests.Select(t => $"- `{t}`")));

        if (test.ExitCode != 0 || total == 0)
        {
            var details = Tail(test.Combined, 3000);
            foreach (var name in failedTests.DefaultIfEmpty(""))
            {
                var cls = name.Split('.').SkipLast(1).LastOrDefault() ?? "";
                var testTask = plan.Tasks.FirstOrDefault(t => t.Kind == "test" && Path.GetFileNameWithoutExtension(t.Path) == cls);
                var targets = testTask is null ? testNodes.Concat(codeNodes) : [PlanNode.NodeId(testTask), .. testTask.DependsOn.Select(d => plan.Tasks.First(t => t.Id == d)).Select(PlanNode.NodeId)];
                foreach (var target in targets)
                    Target(target, $"Test failed: {name}. Decide whether the code or the test is wrong according to the requirements, and fix it. Output:\n{details}");
            }
            return LoopBack(retry, report, $"{failed} test(s) failed");
        }

        if (coverage < minCoverage)
        {
            foreach (var n in testNodes) Target(n, $"Line coverage {coverage:0.0}% is below the policy minimum of {minCoverage}%. Add tests for untested code paths.");
            return LoopBack(retry, report, $"Coverage {coverage:0.0}% < {minCoverage}%");
        }

        if (retry.Count > 0) return LoopBack(retry, report, "Policy/convention violations");

        return NodeResult.Ok($"Build ✅, {total} tests ✅, coverage {coverage:0.0}%")
            .With(ArtifactKeys.Validation, report.ToString())
            .Because($"Validation passed: {total} tests, {coverage:0.0}% line coverage (min {minCoverage}%).");
    }

    private static NodeResult LoopBack(Dictionary<string, List<string>> retry, StringBuilder report, string reason) => new()
    {
        Success = false,
        Error = $"{reason}; sending {retry.Count} task(s) back: {string.Join(", ", retry.Keys)}",
        RetryTargets = retry,
        Summary = report.ToString(),
    };

    private static double ReadCoverage(string resultsDir)
    {
        if (!Directory.Exists(resultsDir)) return 0;
        var file = Directory.EnumerateFiles(resultsDir, "*cobertura*.xml", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null) return 0;
        var rate = XDocument.Load(file).Root?.Attribute("line-rate")?.Value;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? Math.Round(r * 100, 1) : 0;
    }

    private static string Relative(string projectDir, string path)
    {
        try { return Path.GetRelativePath(projectDir, path.Trim()).Replace('\\', '/'); }
        catch { return path; }
    }

    private static int Int(Match m) => m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    private static string Tail(string text, int max = 1500) => text.Length <= max ? text : text[^max..];

    [GeneratedRegex(@"^(?<file>[^\r\n(]+\.cs)\((?<line>\d+),(?<col>\d+)\): error (?<code>[A-Z]+\d+): (?<msg>[^\r\n\[]+)", RegexOptions.Multiline)]
    private static partial Regex BuildError();

    [GeneratedRegex(@"PackageReference\s+Include=""([^""]+)""")]
    private static partial Regex PackageRef();

    [GeneratedRegex(@"total:\s*(\d+)")]
    private static partial Regex TotalRegex();

    [GeneratedRegex(@"failed:\s*(\d+)")]
    private static partial Regex FailedCountRegex();

    [GeneratedRegex(@"^\s*failed\s+(?<name>[\w\.]+)", RegexOptions.Multiline)]
    private static partial Regex FailedTestRegex();
}

/// <summary>LLM code review of the change against the spec. High-severity findings require human approval to proceed.</summary>
public sealed class ReviewNode(AgentServices s) : INodeHandler
{
    private const int MaxChars = 16_000;

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var plan = AgentServices.Read<Plan>(run, ArtifactKeys.Plan);
        var files = new StringBuilder();
        foreach (var t in plan.Tasks.OrderBy(t => t.Kind == "test" ? 1 : 0).ThenBy(t => t.Path, StringComparer.Ordinal))
        {
            var content = run.Get(ArtifactKeys.File(t.Path)) ?? "";
            if (files.Length + content.Length > MaxChars) { files.AppendLine($"// FILE: {t.Path} (omitted for length)"); continue; }
            files.AppendLine($"// FILE: {t.Path} ({t.Action})").AppendLine(content);
        }

        var context = Prompts.Section("SPECIFICATION", JsonText.Serialize(new { spec.Title, spec.FunctionalRequirements, spec.Clarifications })) + "\n" +
                      Prompts.Section("CHANGED FILES", files.ToString());
        var review = await s.AskJsonAsync<Review>(ctx, "reviewer", "senior code reviewer", Prompts.Reviewer, context, ct, 1500);

        var high = review.Findings.Count(f => f.Severity.Equals("high", StringComparison.OrdinalIgnoreCase));
        var result = NodeResult.Ok($"Review: {review.Verdict}, {review.Findings.Count} finding(s), {high} high. {review.Summary}");
        if (high > 0) result.RequestedActions.Add("review.high-risk");
        foreach (var f in review.Findings) result.Because($"Review [{f.Severity}] {f.File}: {f.Issue}");
        return result.With(ArtifactKeys.Review, JsonText.Serialize(review));
    }
}

/// <summary>Release readiness: version, changelog, card/index update, evidence pack, snapshot. Gated by human approval.</summary>
public sealed class ReleaseNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var plan = AgentServices.Read<Plan>(run, ArtifactKeys.Plan);
        var nfr = AgentServices.Read<NfrArtifact>(run, ArtifactKeys.Nfr);
        var review = AgentServices.Read<Review>(run, ArtifactKeys.Review);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);
        var impact = AgentServices.TryRead<Impact>(run, ArtifactKeys.Impact);
        var card = await s.Projects.GetAsync(workspace.Slug, ct) ?? throw new InvalidOperationException("Project card missing");
        var version = $"v{card.NextVersionNumber}";

        var notes = new StringBuilder($"# Release {workspace.Slug} {version}\n\n");
        notes.AppendLine($"**Change type:** {workspace.Mode} · **Base:** {workspace.BaseVersion} · **Deployment profile:** {nfr.Profile} ({nfr.Verdict}{(nfr.Overridden ? ", overridden" : "")})\n");
        notes.AppendLine($"**Requirement:** {run.Require(ArtifactKeys.Requirement)}\n");
        notes.AppendLine("## Functional changes");
        foreach (var f in spec.FunctionalRequirements) notes.AppendLine($"- {f.Id}: {f.Description}");
        notes.AppendLine("\n## Files");
        foreach (var t in plan.Tasks) notes.AppendLine($"- `{t.Path}` ({t.Action})");
        notes.AppendLine("\n## Validation\n" + (run.Get(ArtifactKeys.Validation) ?? "n/a").Replace("# Validation report", "").Trim());
        notes.AppendLine($"\n## Review: {review.Verdict}\n{review.Summary}");
        foreach (var f in review.Findings) notes.AppendLine($"- [{f.Severity}] {f.File}: {f.Issue}");
        notes.AppendLine("\n## Risks & assumptions");
        foreach (var r in (impact?.Risks ?? []).Concat(AgentServices.Read<Design>(run, ArtifactKeys.Design).Risks)) notes.AppendLine($"- Risk: {r}");
        foreach (var a in spec.Assumptions.Concat(nfr.Assumptions)) notes.AppendLine($"- Assumption: {a}");
        notes.AppendLine("\n## Rollback\n" + (workspace.Mode == "brownfield"
            ? $"Restore snapshot `.versions/{workspace.BaseVersion}` (or run checkpoint `{workspace.Checkpoint}`)."
            : "Delete the project folder; no previous version exists."));

        return NodeResult.Ok($"Publish {workspace.Slug} {version}: {plan.Tasks.Count} files, review {review.Verdict}, profile {nfr.Profile}.")
            .With(ArtifactKeys.Release, notes.ToString())
            .Because($"Release candidate {workspace.Slug} {version} prepared.");
    }

    public static Func<RunContext, NodeResult, CancellationToken, Task> Commit(AgentServices s) => async (run, result, ct) =>
    {
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var plan = AgentServices.Read<Plan>(run, ArtifactKeys.Plan);
        var design = AgentServices.Read<Design>(run, ArtifactKeys.Design);
        var nfr = AgentServices.Read<NfrArtifact>(run, ArtifactKeys.Nfr);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);
        var slug = workspace.Slug;
        var projectDir = s.ProjectDir(run);
        var card = await s.Projects.GetAsync(slug, ct) ?? throw new InvalidOperationException("Project card missing");
        var version = $"v{card.NextVersionNumber}";
        var now = s.Time.GetUtcNow();

        if (workspace.Mode == "greenfield")
        {
            card.Name = spec.ProjectName;
            card.Summary = spec.Summary;
        }
        card.Aliases = card.Aliases.Union(spec.Aliases, StringComparer.OrdinalIgnoreCase).ToList();
        card.Capabilities = card.Capabilities.Union(spec.Capabilities, StringComparer.OrdinalIgnoreCase).ToList();
        card.Entities = card.Entities.Union(design.Entities.Select(e => e.Name)).ToList();
        card.Endpoints = ProjectFiles.DiscoverEndpoints(projectDir);
        foreach (var t in plan.Tasks.Where(t => t.Kind == "code"))
            card.ModuleMap[t.Path] = t.Description.Length > 140 ? t.Description[..140] + "…" : t.Description;
        card.DeploymentProfile = nfr.Profile;
        card.CurrentVersion = version;
        var entry = new ProjectVersion(version, workspace.Mode, Shorten(run.Require(ArtifactKeys.Requirement), 160), run.RunId, now, spec.Title);
        card.Versions.Add(entry);
        card.SourceHash = s.Projects.ComputeSourceHash(slug);

        // Evidence pack: everything a reviewer needs to audit this change.
        var evidence = Path.Combine(projectDir, ".runs", run.RunId);
        Directory.CreateDirectory(evidence);
        foreach (var (key, name) in new[]
                 {
                     (ArtifactKeys.Routing, "routing.json"), (ArtifactKeys.Spec, "spec.json"), (ArtifactKeys.Nfr, "nfr.json"),
                     (ArtifactKeys.CapacityReport, "capacity-report.md"), (ArtifactKeys.Impact, "impact.json"), (ArtifactKeys.Design, "design.json"),
                     (ArtifactKeys.Plan, "plan.json"), (ArtifactKeys.Validation, "validation.md"), (ArtifactKeys.Review, "review.json"),
                     (ArtifactKeys.Release, "release-notes.md"),
                 })
        {
            if (run.Get(key) is { } content) await File.WriteAllTextAsync(Path.Combine(evidence, name), content, ct);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "nfr-traceability.md"), Traceability(design), ct);

        var changelog = Path.Combine(projectDir, "CHANGELOG.md");
        var previous = File.Exists(changelog) ? (await File.ReadAllTextAsync(changelog, ct)).Replace("# Changelog\n\n", "") : "";
        await File.WriteAllTextAsync(changelog,
            $"# Changelog\n\n## {version} — {now:yyyy-MM-dd} ({workspace.Mode})\n{spec.Title}\n" +
            string.Join("", spec.FunctionalRequirements.Select(f => $"- {f.Description}\n")) + "\n" + previous, ct);

        await s.Projects.SaveCardAsync(card, ct);
        await s.Projects.AppendHistoryAsync(slug, entry, ct);
        await s.Projects.SnapshotAsync(slug, version, ct);
        run.Settings.TryRemove(Infrastructure.Workspace.WorkspaceRollbackService.CreatedProjectFlag, out _);
    };

    private static string Traceability(Design design)
    {
        var sb = new StringBuilder("# NFR traceability\n\n| NFR | Pattern | Building block | Verified by | Status |\n|---|---|---|---|---|\n");
        foreach (var p in design.NfrPatterns) sb.AppendLine($"| {p.Nfr} | {p.Pattern} | {p.BuildingBlock} | {p.Test} | ✅ validation gate passed |");
        return sb.ToString();
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
