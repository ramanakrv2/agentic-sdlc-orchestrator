using Orchestrator.Core.Engine;
using Orchestrator.Core.Persistence;
using Spectre.Console;

namespace Orchestrator.Cli;

/// <summary>Live progress lines for engine and LLM events.</summary>
public sealed class ConsoleRunObserver : IRunObserver
{
    private static readonly object Gate = new();

    public void OnEvent(RunEvent e)
    {
        var d = e.Data ?? new Dictionary<string, string>();
        string? line = e.Type switch
        {
            EventTypes.RunStarted => $"[bold]▶ Run {e.RunId} started[/]",
            EventTypes.RunResumed => $"[bold]▶ Run {e.RunId} resumed[/]",
            EventTypes.NodeStarted => $"  [grey]→ {e.Stage,-14}[/] {Esc(e.NodeId)}",
            EventTypes.NodeSucceeded => $"  [green]✔ {e.Stage,-14}[/] {Esc(e.NodeId)} [grey]({e.DurationMs / 1000:0.0}s, attempts {d.GetValueOrDefault("attempts")})[/]",
            EventTypes.NodeSkipped => $"  [grey]○ {e.Stage,-14} {Esc(e.NodeId)} skipped (condition false)[/]",
            EventTypes.NodeAttemptFailed => $"  [yellow]✖ {Esc(e.NodeId)} attempt {d.GetValueOrDefault("attempt")} failed:[/] {Esc(Short(d.GetValueOrDefault("error")))}",
            EventTypes.GateFailed => $"  [yellow]⛔ {Esc(e.NodeId)} {d.GetValueOrDefault("kind")} gate '{Esc(d.GetValueOrDefault("gate"))}':[/] {Esc(Short(d.GetValueOrDefault("violations")))}",
            EventTypes.NodeRetried => $"  [yellow]↻ {Esc(e.NodeId)} retry (attempt {d.GetValueOrDefault("attempt")}) with feedback[/]",
            EventTypes.LoopBack => $"  [yellow]⟲ {Esc(e.NodeId)} → re-run {Esc(d.GetValueOrDefault("targets"))} (loop {d.GetValueOrDefault("iteration")}):[/] {Esc(Short(d.GetValueOrDefault("reason")))}",
            EventTypes.FallbackUsed => $"  [magenta]⇢ fallback strategy for {Esc(e.NodeId)}[/]",
            EventTypes.Replanned => $"  [blue]♻ {Esc(e.NodeId)} re-planned: {Esc(d.GetValueOrDefault("reason"))}[/]",
            EventTypes.Expanded => $"  [blue]＋ {Esc(e.NodeId)} expanded graph: {Esc(Short(d.GetValueOrDefault("nodes"), 160))}[/]",
            EventTypes.PolicyBlocked => $"  [red]🛑 policy blocked {Esc(d.GetValueOrDefault("action"))} in {Esc(e.NodeId)}[/]",
            EventTypes.ApprovalResolved => $"  [cyan]✋ {Esc(d.GetValueOrDefault("action"))}: {Esc(d.GetValueOrDefault("outcome"))} by {Esc(d.GetValueOrDefault("actor"))}[/]",
            EventTypes.NodeFailed => $"  [red]✖ {Esc(e.NodeId)} FAILED: {Esc(Short(d.GetValueOrDefault("error"), 300))}[/]",
            EventTypes.RolledBack => $"  [red]⎌ rollback: {Esc(d.GetValueOrDefault("detail"))}[/]",
            EventTypes.SafeStopped => $"[red bold]■ Safe-stopped ({Esc(d.GetValueOrDefault("status"))}): {Esc(Short(d.GetValueOrDefault("reason"), 300))}[/]\n  [grey]State is persisted; fix the cause and run: agent resume {e.RunId}[/]",
            EventTypes.RunCompleted => $"[bold]■ Run {e.RunId} finished: {Esc(d.GetValueOrDefault("status"))}[/]",
            EventTypes.LlmCall => $"    [grey]· llm {Esc(d.GetValueOrDefault("agent"))} via {Esc(d.GetValueOrDefault("model"))} ({Esc(d.GetValueOrDefault("source"))}, {d.GetValueOrDefault("tokens")} tok, {e.DurationMs / 1000:0.0}s)[/]",
            EventTypes.LlmTransientRetry => $"    [yellow]· llm transient retry {d.GetValueOrDefault("attempt")} on {Esc(d.GetValueOrDefault("model"))}: {Esc(Short(d.GetValueOrDefault("error")))}[/]",
            EventTypes.CircuitOpened => $"    [red]· circuit OPEN for {Esc(d.GetValueOrDefault("model"))} ({d.GetValueOrDefault("breakSeconds")}s)[/]",
            EventTypes.CircuitClosed => $"    [green]· circuit closed for {Esc(d.GetValueOrDefault("model"))}[/]",
            EventTypes.LlmModelFallback => $"    [magenta]· llm fallback {Esc(d.GetValueOrDefault("from"))} → {Esc(d.GetValueOrDefault("to"))}[/]",
            _ => null,
        };
        if (line is null) return;
        lock (Gate) AnsiConsole.MarkupLine(line);
    }

    private static string Esc(string? s) => Markup.Escape(s ?? "");
    private static string Short(string? s, int max = 180) => s is null ? "" : (s.Length <= max ? s : s[..max] + "…").Replace("\n", " ").Replace("\r", "");
}

/// <summary>Human approval checkpoint in the terminal. --yes approves automatically (recorded as such in the audit log).</summary>
public sealed class ConsoleApprovalService(bool autoApprove) : IApprovalService
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var panel = new Panel(Markup.Escape(request.Summary))
            {
                Header = new PanelHeader($"[cyan] APPROVAL REQUIRED: {Markup.Escape(request.Action)} ({Markup.Escape(request.NodeId)}) [/]"),
                Border = BoxBorder.Rounded,
            };
            AnsiConsole.Write(panel);
            if (autoApprove)
            {
                AnsiConsole.MarkupLine("  [grey]auto-approved (--yes)[/]");
                return ApprovalDecision.Approve("operator:auto-approve(--yes)");
            }

            var user = Environment.UserName;
            while (true)
            {
                var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title("Decision?")
                    .AddChoices("Approve", "Revise (give feedback)", "Show artifacts", "Reject (rollback)"));
                switch (choice)
                {
                    case "Approve":
                        return ApprovalDecision.Approve($"human:{user}");
                    case "Reject (rollback)":
                        return ApprovalDecision.Reject($"human:{user}", AnsiConsole.Prompt(new TextPrompt<string>("Reason:").AllowEmpty()));
                    case "Revise (give feedback)":
                        return ApprovalDecision.Revise($"human:{user}", AnsiConsole.Prompt(new TextPrompt<string>("What should change?")));
                    default:
                        foreach (var (key, content) in request.Artifacts)
                        {
                            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(key)}[/]");
                            AnsiConsole.WriteLine(content.Length > 4000 ? content[..4000] + "\n…" : content);
                        }
                        break;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Questions to the human. Resolution order: preset answer (--answers file / flags) → default when --yes → interactive prompt.
/// </summary>
public sealed class ConsoleHumanInteraction(IReadOnlyDictionary<string, string> presets, bool useDefaults) : IHumanInteraction
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> AskAsync(string runId, string questionId, string question, IReadOnlyList<string> options, string? defaultAnswer, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            AnsiConsole.MarkupLine($"[cyan]? {Markup.Escape(question)}[/]");
            if (presets.TryGetValue(questionId, out var preset))
            {
                var match = options.FirstOrDefault(o => o.Equals(preset, StringComparison.OrdinalIgnoreCase))
                            ?? options.FirstOrDefault(o => o.StartsWith(preset, StringComparison.OrdinalIgnoreCase)) ?? preset;
                AnsiConsole.MarkupLine($"  [grey]answer (preset): {Markup.Escape(match)}[/]");
                return match;
            }
            if (useDefaults && defaultAnswer is not null)
            {
                AnsiConsole.MarkupLine($"  [grey]answer (default): {Markup.Escape(defaultAnswer)}[/]");
                return defaultAnswer;
            }
            if (options.Count > 0)
            {
                var ordered = defaultAnswer is not null && options.Contains(defaultAnswer)
                    ? options.Where(o => o == defaultAnswer).Concat(options.Where(o => o != defaultAnswer)).ToList()
                    : options.ToList();
                return AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices(ordered));
            }
            var prompt = new TextPrompt<string>(">");
            if (defaultAnswer is not null) prompt.DefaultValue(defaultAnswer);
            return AnsiConsole.Prompt(prompt);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task NotifyAsync(string runId, string message, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[grey]ℹ {Markup.Escape(message)}[/]");
        return Task.CompletedTask;
    }
}
