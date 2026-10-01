using System.Globalization;
using System.Text;
using Orchestrator.Core.Capacity;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Projects;
using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Agents.Nodes;

/// <summary>Greenfield vs brownfield: explicit flag → lexical retrieval over project cards → LLM re-rank → rules → human if unsure.</summary>
public sealed class RouteNode(AgentServices s) : INodeHandler
{
    public const string NewProjectOption = "Create a new project";

    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var requirement = run.Require(ArtifactKeys.Requirement);
        var cards = await s.Projects.ListAsync(ct);
        var routing = new RoutingArtifact();

        if (run.Settings.TryGetValue(Setting.Project, out var explicitSlug) && !string.IsNullOrWhiteSpace(explicitSlug))
        {
            if (cards.All(c => c.Slug != explicitSlug)) return NodeResult.Fail($"Project '{explicitSlug}' does not exist. Known: {string.Join(", ", cards.Select(c => c.Slug))}");
            routing = new RoutingArtifact { Kind = "brownfield", ProjectSlug = explicitSlug, Confidence = 1, Rationale = "Project selected explicitly (--project).", DecidedBy = "operator" };
        }
        else if (run.Settings.TryGetValue(Setting.ForceNew, out var forceNew) && forceNew == "true")
        {
            routing = new RoutingArtifact { Kind = "greenfield", Confidence = 1, Rationale = "New project requested explicitly (--new).", DecidedBy = "operator" };
        }
        else if (cards.Count == 0)
        {
            routing = new RoutingArtifact { Kind = "greenfield", Confidence = 1, Rationale = "Workspace has no projects yet." };
        }
        else
        {
            var intent = IntentClassifier.Classify(requirement);
            var candidates = s.Matcher.FindCandidates(requirement, cards);
            double? llmConfidence = null;
            string? llmChoice = null;
            var decidedBy = "rules";

            if (candidates.Count > 0)
            {
                var context = new StringBuilder()
                    .AppendLine(Prompts.Section("REQUEST", requirement))
                    .AppendLine(Prompts.Section("EXISTING PROJECTS", JsonText.Serialize(candidates.Select(c => new
                    {
                        slug = c.Card.Slug, name = c.Card.Name, summary = c.Card.Summary, capabilities = c.Card.Capabilities, endpoints = c.Card.Endpoints,
                    }))))
                    .ToString();
                var verdict = await s.AskJsonAsync<RouterVerdict>(ctx, "router", "project router", Prompts.Router, context, ct, 400);
                var top = candidates[0].Score;
                switch (verdict.Decision.ToLowerInvariant())
                {
                    case "existing" when candidates.Any(c => c.Card.Slug == verdict.Project):
                        llmChoice = verdict.Project;
                        llmConfidence = Math.Min(1, Math.Round((Math.Clamp(verdict.Confidence, 0, 1) + top) / 2 + 0.15, 3)); // both signals agree
                        break;
                    case "new":
                        llmConfidence = Math.Min(top, 1 - Math.Clamp(verdict.Confidence, 0, 1));
                        break;
                    default:
                        llmConfidence = Math.Min(top, 0.6);
                        break;
                }
                decidedBy = $"lexical+llm ({verdict.Rationale})";
            }

            var decision = RoutingRules.Decide(intent, candidates, llmConfidence, llmChoice);
            routing = new RoutingArtifact
            {
                Kind = decision.Kind == RoutingKind.Brownfield ? "brownfield" : "greenfield",
                ProjectSlug = decision.ProjectSlug,
                Confidence = Math.Round(decision.Confidence, 3),
                Rationale = $"{decision.Rationale} Intent={intent}.",
                Candidates = candidates.Select(c => $"{c.Card.Slug} (score {c.Score:0.00}; matched: {string.Join(",", c.MatchedTerms)})").ToList(),
                DecidedBy = decidedBy,
            };

            if (decision.Kind == RoutingKind.AskHuman)
            {
                var options = candidates.Select(c => c.Card.Slug).Append(NewProjectOption).ToList();
                var question = $"{decision.Rationale} Which project does this request belong to?\n  " + string.Join("\n  ", routing.Candidates);
                var answer = await s.Human.AskAsync(run.RunId, "route", question, options, decision.ProjectSlug ?? NewProjectOption, ct);
                routing.Kind = answer == NewProjectOption ? "greenfield" : "brownfield";
                routing.ProjectSlug = answer == NewProjectOption ? null : answer;
                routing.DecidedBy = "human";
                routing.Rationale += $" Human selected: {answer}.";
            }
        }

        if (routing.Kind == "brownfield") run.ProjectSlug = routing.ProjectSlug;
        else routing.ProjectSlug = null;

        return NodeResult.Ok($"Routing: {routing.Kind} {routing.ProjectSlug}".Trim())
            .With(ArtifactKeys.Routing, JsonText.Serialize(routing))
            .Because($"Routed as {routing.Kind}{(routing.ProjectSlug is null ? "" : $" → {routing.ProjectSlug}")} (confidence {routing.Confidence:0.00}, by {routing.DecidedBy}). {routing.Rationale}");
    }
}

/// <summary>Requirement → normalised spec. Ambiguities go to the human; answers are folded back in by a second pass.</summary>
public sealed class AnalyzeNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var routing = AgentServices.Read<RoutingArtifact>(run, ArtifactKeys.Routing);
        var context = new StringBuilder().AppendLine(Prompts.Section("REQUIREMENT", run.Require(ArtifactKeys.Requirement)));
        if (routing.ProjectSlug is { } slug && await s.Projects.GetAsync(slug, ct) is { } card)
        {
            context.AppendLine(Prompts.Section("EXISTING PROJECT", JsonText.Serialize(new
            {
                card.Name, card.Summary, card.Capabilities, card.Entities, card.Endpoints, card.CurrentVersion,
            })));
        }

        var spec = await s.AskJsonAsync<Spec>(ctx, "analyst", "requirements analyst", Prompts.Analyst, context.ToString(), ct);
        var result = NodeResult.Ok();

        if (spec.Ambiguities.Count > 0)
        {
            var answers = new List<Clarification>();
            foreach (var a in spec.Ambiguities.Take(3))
            {
                var answer = await s.Human.AskAsync(run.RunId, $"clarify:{a.Id}", a.Question, a.Options, a.Default, ct);
                answers.Add(new Clarification(a.Question, answer));
                result.Because($"Clarification {a.Id}: \"{a.Question}\" → \"{answer}\"");
            }

            var refineContext = Prompts.Section("DRAFT SPECIFICATION", JsonText.Serialize(spec)) + "\n" +
                                Prompts.Section("ANSWERS", string.Join("\n", answers.Select((x, i) => $"{spec.Ambiguities[i].Id}: {x.Question} -> {x.Answer}")));
            var refined = await s.AskJsonAsync<Spec>(ctx, "analyst-refine", "requirements analyst", Prompts.AnalystRefine, refineContext, ct);
            refined.Ambiguities = [];
            foreach (var answer in answers.Where(a => refined.Clarifications.All(c => c.Question != a.Question)))
                refined.Clarifications.Add(answer);
            spec = refined;
        }

        foreach (var assumption in spec.Assumptions) result.Because($"Assumption: {assumption}");
        result.Summary = $"{spec.Title}: {spec.FunctionalRequirements.Count} functional requirements, {spec.Assumptions.Count} assumptions, {spec.Clarifications.Count} clarifications.";
        return result.With(ArtifactKeys.Spec, JsonText.Serialize(spec));
    }

    public static readonly IGate Gate = Orchestrator.Core.Engine.Gate.Sync("spec-schema", (_, r) =>
    {
        if (r?.Artifacts.GetValueOrDefault(ArtifactKeys.Spec) is not { } json) return GateResult.Fail("spec missing");
        var spec = JsonText.Deserialize<Spec>(json);
        var v = new List<string>();
        if (string.IsNullOrWhiteSpace(spec.Title)) v.Add("title is empty");
        if (string.IsNullOrWhiteSpace(spec.ProjectName)) v.Add("projectName is empty");
        if (spec.FunctionalRequirements.Count == 0) v.Add("at least one functional requirement is required");
        v.AddRange(spec.FunctionalRequirements.Where(f => string.IsNullOrWhiteSpace(f.Description) || string.IsNullOrWhiteSpace(f.Acceptance))
            .Select(f => $"{f.Id}: description and acceptance criteria are required"));
        if (spec.Ambiguities.Count > 0) v.Add("unresolved ambiguities remain");
        return GateResult.From(v);
    });
}

/// <summary>
/// Deployment target + load questions, then deterministic capacity/feasibility analysis. Not-feasible blocks the run until a
/// human changes the target, lowers the requirement, overrides (audited) or aborts. Carries the spec approval checkpoint.
/// </summary>
public sealed class NfrNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var routing = AgentServices.Read<RoutingArtifact>(run, ArtifactKeys.Routing);
        var card = routing.ProjectSlug is null ? null : await s.Projects.GetAsync(routing.ProjectSlug, ct);
        var result = NodeResult.Ok();
        var assumptions = new List<string>();

        async Task<string> Value(string key, string question, IReadOnlyList<string> options, string? fromSpec, string fallback)
        {
            if (run.Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (!string.IsNullOrWhiteSpace(fromSpec)) return fromSpec;
            return await s.Human.AskAsync(run.RunId, key, question, options, fallback, ct);
        }

        var profileId = await Value(Setting.Deploy, "Where will this service be deployed?", s.Profiles.All.Select(p => p.Id).ToList(), null, card?.DeploymentProfile ?? "local");
        var loadText = await Value(Setting.Load, "Expected requests per day? (e.g. 100k, 1M, 50 rps)", [], spec.Nfr.RequestsPerDay, "100k");
        var availabilityText = await Value(Setting.Availability, "Availability target (%)?", ["99.0", "99.9", "99.99"], spec.Nfr.Availability, "99.0");

        var load = FeasibilityAnalyzer.ParseLoad(loadText) ?? 100_000;
        var availability = double.TryParse(availabilityText.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var av) ? av : 99.0;
        var ratio = double.TryParse(run.Settings.GetValueOrDefault(Setting.ReadWriteRatio), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 100;
        var retention = int.TryParse(run.Settings.GetValueOrDefault(Setting.RetentionDays), out var rd) ? rd : 90;
        if (!run.Settings.ContainsKey(Setting.ReadWriteRatio)) assumptions.Add($"Read:write ratio {ratio}:1 (typical for read-heavy services).");
        if (!run.Settings.ContainsKey(Setting.RetentionDays)) assumptions.Add($"Data retention {retention} days.");
        assumptions.Add("Peak traffic = 10× the daily average.");

        var profile = s.Profiles.Get(profileId);
        var nfr = new NfrInput { RequestsPerDay = load, AvailabilityTarget = availability, ReadWriteRatio = ratio, RetentionDays = retention, Assumptions = assumptions };
        var overridden = false;
        FeasibilityReport report;

        for (var round = 0; ; round++)
        {
            report = FeasibilityAnalyzer.Analyze(nfr, profile, s.Profiles.All);
            if (report.Verdict != Verdict.NotFeasible || round >= 4) break;

            var failing = string.Join("; ", report.Checks.Where(c => c.Verdict == Verdict.NotFeasible).Select(c => $"{c.Dimension} needs {c.Required:N1} {c.Unit} (limit {c.Limit:N1})"));
            var switchOption = report.Recommended is { } rec ? $"Switch deployment to '{rec.Id}'" : null;
            var options = new[] { switchOption, "Lower the load requirement", "Proceed anyway (override, audited)", "Abort the run" }.OfType<string>().ToList();
            var choice = await s.Human.AskAsync(run.RunId, "feasibility",
                $"NOT FEASIBLE on '{profile.Id}': {failing}. How do you want to proceed?", options, switchOption ?? "Abort the run", ct);

            if (choice == switchOption)
            {
                result.Because($"Feasibility: switched deployment {profile.Id} → {report.Recommended!.Id} ({failing}).");
                profile = report.Recommended!;
            }
            else if (choice.StartsWith("Lower"))
            {
                var lowered = await s.Human.AskAsync(run.RunId, "load-lower", "New expected requests per day?", [], "1M", ct);
                nfr = nfr with { RequestsPerDay = FeasibilityAnalyzer.ParseLoad(lowered) ?? nfr.RequestsPerDay };
                result.Because($"Feasibility: requirement lowered to {lowered}/day.");
            }
            else if (choice.StartsWith("Proceed"))
            {
                overridden = true;
                result.RequestedActions.Add("feasibility.override");
                result.Because($"Feasibility OVERRIDE on '{profile.Id}' despite: {failing}.");
                break;
            }
            else
            {
                return NodeResult.Stop($"Aborted by operator: requirement not feasible on '{profile.Id}' ({failing}).");
            }
        }

        if (card is not null && !card.DeploymentProfile.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
            result.RequestedActions.Add("deploy.profile.change");

        var artifact = new NfrArtifact
        {
            Profile = profile.Id, Verdict = report.Verdict.ToString(), RequestsPerDay = nfr.RequestsPerDay, PeakRps = Math.Round(report.PeakRps, 1),
            AvailabilityTarget = nfr.AvailabilityTarget, Overridden = overridden, Assumptions = assumptions,
        };
        result.Because($"Capacity verdict on '{profile.Id}': {report.Verdict} (peak {report.PeakRps:N0} RPS, storage {report.StorageGb:N1} GB).");
        result.Summary = SpecSummary(spec) + $"\nDeployment: {profile.Name} — capacity verdict {report.Verdict} (avg {report.AvgRps:N1} RPS, peak {report.PeakRps:N0} RPS, {report.StorageGb:N1} GB).";
        return result
            .With(ArtifactKeys.Nfr, JsonText.Serialize(artifact))
            .With(ArtifactKeys.CapacityReport, report.ToMarkdown());
    }

    public static string SpecSummary(Spec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{spec.Title} — {spec.Summary}");
        foreach (var f in spec.FunctionalRequirements) sb.AppendLine($"  {f.Id}: {f.Description}");
        foreach (var c in spec.Clarifications) sb.AppendLine($"  Clarified: {c.Question} → {c.Answer}");
        foreach (var a in spec.Assumptions) sb.AppendLine($"  Assumption: {a}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Creates the project folder from the template (greenfield) or checkpoints the existing one (brownfield).</summary>
public sealed class WorkspaceNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var routing = AgentServices.Read<RoutingArtifact>(run, ArtifactKeys.Routing);
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var nfr = AgentServices.Read<NfrArtifact>(run, ArtifactKeys.Nfr);
        var result = NodeResult.Ok();
        var artifact = new WorkspaceArtifact { Mode = routing.Kind };

        if (routing.Kind == "greenfield")
        {
            var slug = await s.Projects.ReserveSlugAsync(Slug.From(string.IsNullOrWhiteSpace(spec.ProjectName) ? spec.Title : spec.ProjectName), ct);
            run.ProjectSlug = slug;
            run.Settings[WorkspaceRollbackService.CreatedProjectFlag] = "true";
            CopyTemplate(s.Paths.TemplateDir, s.ProjectDir(run));
            await s.Projects.SaveCardAsync(new ProjectCard
            {
                Slug = slug, Name = spec.ProjectName, Summary = spec.Summary, Aliases = spec.Aliases, Capabilities = spec.Capabilities,
                DeploymentProfile = nfr.Profile, CurrentVersion = "v0",
            }, ct);
            artifact.Slug = slug;
            result.RequestedActions.Add("project.create");
            result.Because($"Created project '{slug}' from template '{Path.GetFileName(s.Paths.TemplateDir)}'.");
            result.Summary = $"Create new project workspace/{slug}";
        }
        else
        {
            var slug = routing.ProjectSlug!;
            run.ProjectSlug = slug;
            var card = await s.Projects.GetAsync(slug, ct) ?? throw new InvalidOperationException($"Project card for '{slug}' missing.");
            var label = $"pre-{run.RunId}";
            await s.Projects.SnapshotAsync(slug, label, ct);
            run.Settings[WorkspaceRollbackService.CheckpointLabelKey] = label;

            var currentHash = s.Projects.ComputeSourceHash(slug);
            if (!string.IsNullOrEmpty(card.SourceHash) && card.SourceHash != currentHash)
            {
                card.Endpoints = ProjectFiles.DiscoverEndpoints(s.ProjectDir(run));
                card.SourceHash = currentHash;
                await s.Projects.SaveCardAsync(card, ct);
                artifact.CardWasStale = true;
                result.Because("Project card was stale (code changed outside the orchestrator); endpoints refreshed from code.");
            }

            artifact.Slug = slug;
            artifact.BaseVersion = card.CurrentVersion;
            artifact.Checkpoint = label;
            result.RequestedActions.Add("project.modify");
            result.Because($"Checkpoint '{label}' taken of {slug} {card.CurrentVersion} before changes.");
            result.Summary = $"Modify existing project '{slug}' ({card.CurrentVersion}) — checkpoint {label} taken for rollback.";
        }

        artifact.ExistingFeatureFiles = ProjectFiles.FeatureFiles(s.ProjectDir(run)).ToList();
        return result.With(ArtifactKeys.Workspace, JsonText.Serialize(artifact));
    }

    private static void CopyTemplate(string source, string target)
    {
        string[] skip = ["bin", "obj", "TestResults", ".vs"];
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(skip.Contains)) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
