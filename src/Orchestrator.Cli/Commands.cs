using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Agents;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Metrics;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Projects;
using Orchestrator.Infrastructure;
using Orchestrator.Infrastructure.Llm;
using Spectre.Console;

namespace Orchestrator.Cli;

public sealed class Commands(IServiceProvider sp, CliArgs cli)
{
    private IRunStore Runs => sp.GetRequiredService<IRunStore>();
    private IEventStore Events => sp.GetRequiredService<IEventStore>();
    private IAuditLog Audit => sp.GetRequiredService<IAuditLog>();
    private TimeProvider Time => sp.GetRequiredService<TimeProvider>();

    // ------------------------------------------------------------------ run / resume / revise

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var requirement = cli.Get("file") is { } file ? await File.ReadAllTextAsync(file, ct) : string.Join(' ', cli.Positionals);
        if (string.IsNullOrWhiteSpace(requirement))
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] agent run \"<requirement>\" [options]   (see: agent help)");
            return 1;
        }

        var now = Time.GetUtcNow();
        var runId = $"r-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var context = new RunContext { RunId = runId, Requirement = requirement.Replace("\r\n", "\n").Trim(), WorkspaceRoot = sp.GetRequiredService<IProjectRegistry>().WorkspaceRoot };
        foreach (var (k, v) in cli.RunSettings()) context.Settings[k] = v;
        var record = new RunRecord { RunId = runId, Requirement = context.Requirement, StartedAt = now, Settings = new(context.Settings) };

        var artifact = Artifact.Create(ArtifactKeys.Requirement, context.Requirement, "human", 1, now, "");
        context.Artifacts[artifact.Key] = artifact;
        await Runs.SaveRunAsync(record, ct);
        await Runs.SaveArtifactAsync(runId, artifact, ct);
        await Audit.AppendAsync(runId, $"human:{Environment.UserName}", "requirement.submitted", runId, context.Requirement, ct);

        AnsiConsole.Write(new Rule($"[bold]Agentic SDLC run {runId}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Requirement:[/] {Markup.Escape(context.Requirement)}");
        AnsiConsole.MarkupLine($"[grey]LLM mode:[/] {sp.GetRequiredService<LlmOptions>().Mode} · [grey]settings:[/] {Markup.Escape(string.Join(", ", context.Settings.Select(kv => $"{kv.Key}={kv.Value}")))}");
        return await ExecuteAsync(context, record, null, ct);
    }

    public async Task<int> ResumeAsync(CancellationToken ct)
    {
        var runId = cli.Positionals.FirstOrDefault() ?? (await Runs.ListRunsAsync(1, ct)).FirstOrDefault()?.RunId;
        var record = runId is null ? null : await Runs.GetRunAsync(runId, ct);
        if (record is null) { AnsiConsole.MarkupLine("[red]Run not found.[/]"); return 1; }
        if (record.Status == RunStatus.Succeeded && !cli.Has("force"))
        {
            AnsiConsole.MarkupLine("[yellow]Run already succeeded. Use 'agent revise' to change an input, then resume.[/]");
            return 0;
        }

        var context = new RunContext
        {
            RunId = record.RunId, Requirement = record.Requirement, ProjectSlug = record.ProjectSlug,
            WorkspaceRoot = sp.GetRequiredService<IProjectRegistry>().WorkspaceRoot,
        };
        foreach (var (k, v) in record.Settings) context.Settings[k] = v;
        foreach (var a in await Runs.GetArtifactsAsync(record.RunId, ct)) context.Artifacts[a.Key] = a;
        var states = await Runs.GetNodeStatesAsync(record.RunId, ct);

        record.RetriesUsed = 0; // a human decided to resume: fresh retry budget (audited)
        await Audit.AppendAsync(record.RunId, $"human:{Environment.UserName}", "run.resume", record.RunId,
            $"Resumed from {record.Status}: {record.StatusReason}. Retry budget reset.", ct);
        AnsiConsole.Write(new Rule($"[bold]Resuming {record.RunId}[/] (was {record.Status})").LeftJustified());
        return await ExecuteAsync(context, record, states, ct);
    }

    /// <summary>Changes an upstream input of an existing run; on resume, the engine re-plans everything downstream of it.</summary>
    public async Task<int> ReviseAsync(CancellationToken ct)
    {
        var runId = cli.Positionals.FirstOrDefault();
        var nodeId = cli.Get("node");
        var record = runId is null ? null : await Runs.GetRunAsync(runId, ct);
        if (record is null || nodeId is null)
        {
            AnsiConsole.MarkupLine("Usage: agent revise <runId> --node <nodeId> [--set key=value]... [--feedback \"text\"]");
            return 1;
        }

        foreach (var kv in cli.All("set"))
        {
            var idx = kv.IndexOf('=');
            if (idx > 0) record.Settings[kv[..idx]] = kv[(idx + 1)..];
        }
        await Runs.SaveRunAsync(record, ct);

        var states = await Runs.GetNodeStatesAsync(runId!, ct);
        var state = states.FirstOrDefault(s => s.NodeId == nodeId) ?? new NodeState { NodeId = nodeId };
        state.Status = NodeStatus.Pending;
        if (cli.Get("feedback") is { } feedback) state.PendingFeedback = [$"Human reviewer requested changes: {feedback}"];
        await Runs.SaveNodeStateAsync(runId!, state, ct);

        var detail = $"Node '{nodeId}' re-queued. Settings: {string.Join(", ", cli.All("set"))}. Feedback: {cli.Get("feedback")}";
        await Audit.AppendAsync(runId!, $"human:{Environment.UserName}", "run.revised", nodeId, detail, ct);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(detail)}[/]\nNow run: [bold]agent resume {runId}[/] — downstream nodes whose inputs change are re-planned automatically.");
        return 0;
    }

    private async Task<int> ExecuteAsync(RunContext context, RunRecord record, IReadOnlyCollection<NodeState>? states, CancellationToken ct)
    {
        var graph = SdlcPipeline.Build(sp.GetRequiredService<AgentServices>());
        var result = await sp.GetRequiredService<WorkflowEngine>().RunAsync(graph, context, record, states, ct);

        var metrics = MetricsCalculator.Compute(await Events.GetEventsAsync(result.RunId, CancellationToken.None));
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Run", "Value");
        table.AddRow("Status", Color(result.Status));
        table.AddRow("Project", Markup.Escape(result.ProjectSlug ?? "-"));
        if (result.StatusReason is { } reason) table.AddRow("Reason", Markup.Escape(reason));
        table.AddRow("Nodes", $"{metrics.NodeSuccesses} succeeded / {metrics.NodeExecutions} executed");
        table.AddRow("Retries / loop-backs / fallbacks", $"{metrics.SemanticRetries} / {metrics.LoopBacks} / {metrics.Fallbacks}");
        table.AddRow("LLM calls (live / replay)", $"{metrics.LlmCalls} ({metrics.LlmLiveCalls} / {metrics.LlmReplayCalls})");
        table.AddRow("End-to-end", Ms(metrics.AvgEndToEndMs) + $" (agent time {Ms(metrics.AvgAgentTimeMs)}, human wait {Ms(metrics.HumanWaitMs)})");
        if (result.Status == RunStatus.Succeeded && result.ProjectSlug is { } slug)
            table.AddRow("Evidence", Markup.Escape($"workspace/{slug}/.runs/{result.RunId}/"));
        AnsiConsole.Write(table);
        if (result.Status == RunStatus.Succeeded && result.ProjectSlug is { } p)
            AnsiConsole.MarkupLine($"[grey]Try it:[/] dotnet run --project workspace/{Markup.Escape(p)}/src/App   [grey](http://localhost:5080)[/]");

        return result.Status switch { RunStatus.Succeeded => 0, RunStatus.Rejected => 3, _ => 2 };
    }

    // ------------------------------------------------------------------ inspection

    public async Task<int> StatusAsync(CancellationToken ct)
    {
        if (cli.Positionals.FirstOrDefault() is not { } runId)
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Run", "Status", "Project", "Started (UTC)", "Duration", "Requirement / reason");
            foreach (var r in await Runs.ListRunsAsync(30, ct))
                table.AddRow(r.RunId, Color(r.Status), Markup.Escape(r.ProjectSlug ?? "-"), r.StartedAt.ToString("yyyy-MM-dd HH:mm"),
                    r.CompletedAt is { } c ? Ms((c - r.StartedAt).TotalMilliseconds) : "-",
                    Markup.Escape(Short(r.StatusReason ?? r.Requirement, 70)));
            AnsiConsole.Write(table);
            return 0;
        }

        var record = await Runs.GetRunAsync(runId, ct);
        if (record is null) { AnsiConsole.MarkupLine("[red]Run not found.[/]"); return 1; }
        AnsiConsole.MarkupLine($"[bold]{record.RunId}[/] {Color(record.Status)} — {Markup.Escape(record.Requirement)}");
        var nodes = new Table().Border(TableBorder.Rounded).AddColumns("Node", "Status", "Attempts", "Fallback", "Last error");
        foreach (var s in (await Runs.GetNodeStatesAsync(runId, ct)).OrderBy(s => s.StartedAt ?? DateTimeOffset.MaxValue))
            nodes.AddRow(Markup.Escape(s.NodeId), s.Status.ToString(), s.Attempts.ToString(), s.UseFallback ? "yes" : "", Markup.Escape(Short(s.LastError ?? "", 80)));
        AnsiConsole.Write(nodes);
        var artifacts = new Table().Border(TableBorder.Rounded).AddColumns("Artifact", "Version", "Hash", "Produced by");
        foreach (var a in (await Runs.GetArtifactsAsync(runId, ct)).Where(a => !a.Key.StartsWith(ArtifactKeys.FilePrefix) || cli.Has("files")))
            artifacts.AddRow(Markup.Escape(a.Key), a.Version.ToString(), a.Hash[..10], Markup.Escape(a.ProducedBy));
        AnsiConsole.Write(artifacts);
        return 0;
    }

    public async Task<int> MetricsAsync(CancellationToken ct)
    {
        var m = MetricsCalculator.Compute(await Events.GetEventsAsync(cli.Get("run"), ct));
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Reliability metric", "Value");
        t.AddRow("Runs (succeeded / safe-stopped / rejected)", $"{m.Runs} ({m.Succeeded} / {m.SafeStopped} / {m.Rejected})");
        t.AddRow("Run success rate", CliArgs.Pct(m.RunSuccessRate));
        t.AddRow("Node success rate", $"{CliArgs.Pct(m.NodeSuccessRate)} ({m.NodeSuccesses}/{m.NodeExecutions})");
        t.AddRow("Semantic retries / loop-backs", $"{m.SemanticRetries} / {m.LoopBacks}  ({m.RetriesPerRun:0.0} per run)");
        t.AddRow("Transient LLM retries", m.TransientRetries.ToString());
        t.AddRow("Circuit-breaker opens", m.CircuitOpens.ToString());
        t.AddRow("Fallbacks (node / LLM model)", $"{m.Fallbacks} / {m.ModelFallbacks}");
        t.AddRow("Rollbacks (frequency)", $"{m.Rollbacks} ({m.RollbackFrequency:0.00} per run)");
        t.AddRow("Gate failures / policy blocks", $"{m.GateFailures} / {m.PolicyBlocks}");
        t.AddRow("Re-plans", m.Replans.ToString());
        t.AddRow("MTTR (failure → recovery)", $"{Ms(m.MttrMs)} over {m.Recoveries} recoveries");
        t.AddRow("End-to-end latency avg / max", $"{Ms(m.AvgEndToEndMs)} / {Ms(m.MaxEndToEndMs)}");
        t.AddRow("Agent time (excl. human wait)", Ms(m.AvgAgentTimeMs));
        t.AddRow("Human approval wait (total, n)", $"{Ms(m.HumanWaitMs)} ({m.Approvals})");
        t.AddRow("LLM calls live / replay, tokens, avg latency", $"{m.LlmLiveCalls} / {m.LlmReplayCalls}, {m.LlmTokens:N0}, {Ms(m.AvgLlmMs)}");
        AnsiConsole.Write(t);

        var stages = new Table().Border(TableBorder.Rounded).AddColumns("Stage", "Executions", "Success", "Attempt failures", "Avg", "Max");
        foreach (var s in m.Stages)
            stages.AddRow(s.Stage, s.Executions.ToString(), CliArgs.Pct(s.SuccessRate), s.AttemptFailures.ToString(), Ms(s.AvgMs), Ms(s.MaxMs));
        AnsiConsole.Write(stages);

        if (cli.Get("json") is { } path)
        {
            await File.WriteAllTextAsync(path, JsonText.Serialize(m), ct);
            AnsiConsole.MarkupLine($"[grey]Metrics written to {Markup.Escape(path)}[/]");
        }
        return 0;
    }

    public async Task<int> AuditAsync(CancellationToken ct)
    {
        if (cli.Has("verify"))
        {
            var v = await Audit.VerifyAsync(ct);
            AnsiConsole.MarkupLine(v.IsValid ? $"[green]✔ {Markup.Escape(v.Message)}[/]" : $"[red]✖ {Markup.Escape(v.Message)}[/]");
            return v.IsValid ? 0 : 4;
        }
        var entries = await Audit.GetEntriesAsync(cli.Get("run") ?? cli.Positionals.FirstOrDefault(), ct);
        var tail = int.TryParse(cli.Get("tail"), out var n) ? n : 60;
        var t = new Table().Border(TableBorder.Rounded).AddColumns("#", "Time (UTC)", "Actor", "Action", "Target", "Details", "Hash");
        foreach (var e in entries.TakeLast(tail))
            t.AddRow(e.Sequence.ToString(), e.Timestamp.ToString("HH:mm:ss"), Markup.Escape(e.Actor), Markup.Escape(e.Action), Markup.Escape(e.Target),
                Markup.Escape(Short(e.Details, 90)), e.Hash[..8]);
        AnsiConsole.Write(t);
        return 0;
    }

    /// <summary>Decision lineage for an artifact: every version, who produced it, from which inputs, and the recorded rationale.</summary>
    public async Task<int> LineageAsync(CancellationToken ct)
    {
        var runId = cli.Positionals.ElementAtOrDefault(0);
        var key = cli.Positionals.ElementAtOrDefault(1);
        if (runId is null || key is null) { AnsiConsole.MarkupLine("Usage: agent lineage <runId> <artifactKey>   e.g. agent lineage r-... design"); return 1; }
        var history = (await Runs.GetArtifactHistoryAsync(runId, ct)).Where(a => a.Key == key).ToList();
        var audit = await Audit.GetEntriesAsync(runId, ct);
        foreach (var a in history)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(a.Key)} v{a.Version}[/] hash {a.Hash[..12]} produced by [cyan]{Markup.Escape(a.ProducedBy)}[/] at {a.CreatedAt:HH:mm:ss} from inputs {a.InputFingerprint[..Math.Min(12, a.InputFingerprint.Length)]}");
            foreach (var d in audit.Where(e => e.Target == a.ProducedBy && e.Action is "decision" or "approval.approved" or "approval.revise" or "node.completed"))
                AnsiConsole.MarkupLine($"   [grey]{d.Timestamp:HH:mm:ss} {Markup.Escape(d.Actor)} {Markup.Escape(d.Action)}:[/] {Markup.Escape(Short(d.Details, 160))}");
        }
        if (history.Count == 0) AnsiConsole.MarkupLine("[yellow]No such artifact in this run.[/]");
        return 0;
    }

    public async Task<int> ProjectsAsync(CancellationToken ct)
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Slug", "Name", "Version", "Profile", "Endpoints", "Capabilities");
        foreach (var c in await sp.GetRequiredService<IProjectRegistry>().ListAsync(ct))
            t.AddRow(Markup.Escape(c.Slug), Markup.Escape(c.Name), c.CurrentVersion, c.DeploymentProfile, c.Endpoints.Count.ToString(), Markup.Escape(Short(string.Join(", ", c.Capabilities), 60)));
        AnsiConsole.Write(t);
        return 0;
    }

    // ------------------------------------------------------------------ environment

    public async Task<int> DoctorAsync(CancellationToken ct)
    {
        var options = sp.GetRequiredService<LlmOptions>();
        var home = sp.GetRequiredService<HomeDirectory>().Path;
        var ok = true;
        void Check(bool pass, string what, string hint = "")
        {
            ok &= pass;
            AnsiConsole.MarkupLine(pass ? $"[green]✔[/] {Markup.Escape(what)}" : $"[red]✖[/] {Markup.Escape(what)} [grey]{Markup.Escape(hint)}[/]");
        }

        var dotnet = await sp.GetRequiredService<IProcessRunner>().RunAsync("dotnet", "--version", home, TimeSpan.FromSeconds(30), ct);
        Check(dotnet.ExitCode == 0 && dotnet.StdOut.Trim().StartsWith("10."), $".NET SDK {dotnet.StdOut.Trim()}", "install .NET 10 SDK");
        Check(File.Exists(Path.Combine(home, "policy.yaml")), "policy.yaml found");
        Check(Directory.Exists(Path.Combine(home, "templates", "webapi")), "web API template found");
        Check(sp.GetRequiredService<IProfileCatalog>().All.Count > 0, $"{sp.GetRequiredService<IProfileCatalog>().All.Count} deployment profiles loaded");
        var golden = sp.GetRequiredService<ReplayCache>().Golden.Count;
        Check(golden > 0, $"replay cache: {golden} curated responses (llm-cache/golden.json)");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync(options.Endpoint.TrimEnd('/') + "/models", ct);
            Check(true, $"Ollama reachable at {options.Endpoint}");
            foreach (var model in new[] { options.PrimaryModel, options.FallbackModel }.OfType<string>())
                Check(json.Contains(model), $"model {model} available", $"run: ollama pull {model}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Check(false, $"Ollama not reachable at {options.Endpoint}", "install Ollama or use --llm replay");
        }
        var ctx = Environment.GetEnvironmentVariable("OLLAMA_CONTEXT_LENGTH");
        Check(int.TryParse(ctx, out var c) && c >= 8192, $"OLLAMA_CONTEXT_LENGTH={ctx ?? "(unset)"}", "setx OLLAMA_CONTEXT_LENGTH 8192 (prompts are truncated otherwise)");
        return ok ? 0 : 1;
    }

    /// <summary>Authoring helper for curated replay entries (golden fixtures).</summary>
    public async Task<int> FixturesAsync(CancellationToken ct)
    {
        var cache = sp.GetRequiredService<LlmOptions>().CacheDirectory;
        var pending = Path.Combine(cache, "pending");
        var golden = cache;
        var sub = cli.Positionals.FirstOrDefault();

        if (sub == "list")
        {
            foreach (var f in Directory.Exists(pending) ? Directory.GetFiles(pending) : []) AnsiConsole.WriteLine(Path.GetFileName(f));
            return 0;
        }
        if (sub == "fill-code" && cli.Get("source") is { } source)
        {
            // Coder/tester prompts name their target file; answer them with reviewed reference files.
            var filled = 0;
            foreach (var f in Directory.Exists(pending) ? Directory.GetFiles(pending, "*.txt") : [])
            {
                var name = Path.GetFileName(f);
                if (!name.StartsWith("coder-") && !name.StartsWith("tester-")) continue;
                var text = await File.ReadAllTextAsync(f, ct);
                if (text.Contains("PREVIOUS ATTEMPT WAS REJECTED")) { File.Delete(f); continue; } // retries are not curated
                var target = Regex.Match(text, @"### TARGET FILE\s*\n(?<p>\S+)").Groups["p"].Value;
                var reference = Path.Combine(source, target);
                if (!File.Exists(reference)) { AnsiConsole.MarkupLine($"[yellow]no reference for {Markup.Escape(target)}[/]"); continue; }
                await WriteGoldenAsync(golden, f, "```csharp\n" + (await File.ReadAllTextAsync(reference, ct)).Replace("\r\n", "\n").TrimEnd() + "\n```\n", ct);
                filled++;
            }
            AnsiConsole.MarkupLine($"[green]{filled} coder/tester fixtures written.[/]");
            return 0;
        }
        if (sub == "answer" && cli.Positionals.Count >= 3)
        {
            var pendingFile = Path.Combine(pending, cli.Positionals[1]);
            await WriteGoldenAsync(golden, pendingFile, await File.ReadAllTextAsync(cli.Positionals[2], ct), ct);
            AnsiConsole.MarkupLine("[green]Fixture written.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine("Usage: agent fixtures list | fill-code --source <dir> | answer <pendingFile> <responseFile>");
        return 1;
    }

    private Task WriteGoldenAsync(string golden, string pendingFile, string response, CancellationToken ct)
    {
        sp.GetRequiredService<ReplayCache>().AddGolden(Path.GetFileNameWithoutExtension(pendingFile), response.TrimEnd() + "\n");
        File.Delete(pendingFile);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ helpers

    public static int Unknown(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
        PrintHelp();
        return 1;
    }

    public static void PrintHelp() => AnsiConsole.MarkupLine("""
        [bold]agent[/] — governed agentic SDLC orchestrator

        [bold]agent run[/] "<requirement>" [[options]]     Run the SDLC pipeline for a requirement (greenfield or brownfield)
            --project <slug>        force brownfield on this project      --new            force a new project
            --deploy <profile>      local | kubernetes                --load <n>       e.g. 1M/day, 50rps
            --availability <pct>    e.g. 99.9                              --yes            auto-approve + accept defaults
            --answers <file.json>   preset answers to human questions     --answer id=val  single preset answer
            --llm auto|live|replay  LLM source (default auto)              --chaos fault=0.3,latency=0.2  inject LLM faults
            --model <name>          primary Ollama model                   --parallel <n>   max parallel nodes
        [bold]agent resume[/] [[runId]]                    Resume a safe-stopped run (default: latest)
        [bold]agent revise[/] <runId> --node <id> [[--set k=v]] [[--feedback "..."]]   Change an upstream input → re-plan on resume
        [bold]agent status[/] [[runId]] [[--files]]          Runs, or node states + artifacts of one run
        [bold]agent metrics[/] [[--run id]] [[--json file]]  Reliability metrics (success rate, retries, rollbacks, MTTR, latency)
        [bold]agent audit[/] [[--run id]] [[--verify]]       Hash-chained audit trail; --verify checks tamper evidence
        [bold]agent lineage[/] <runId> <artifact>        Decision lineage of an artifact (spec, design, plan, file:...)
        [bold]agent projects[/]                          Project cards in the workspace
        [bold]agent doctor[/]                            Check prerequisites (.NET, Ollama, models, context length)
        """);

    private static string Color(RunStatus s) => s switch
    {
        RunStatus.Succeeded => "[green]Succeeded[/]",
        RunStatus.Running => "[blue]Running[/]",
        RunStatus.Rejected => "[red]Rejected[/]",
        _ => $"[yellow]{s}[/]",
    };

    private static string Ms(double? ms) => ms is null ? "-" : ms < 1000 ? $"{ms:0} ms" : ms < 120_000 ? $"{ms / 1000:0.0} s" : $"{ms / 60_000:0.0} min";
    private static string Short(string s, int max) => (s.Length <= max ? s : s[..max] + "…").Replace("\n", " ");
}
