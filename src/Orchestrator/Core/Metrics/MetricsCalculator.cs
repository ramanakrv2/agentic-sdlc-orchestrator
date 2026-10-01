using Orchestrator.Core.Persistence;

namespace Orchestrator.Core.Metrics;

public sealed record StageMetrics(string Stage, int Executions, int Succeeded, int AttemptFailures, double AvgMs, double MaxMs)
{
    public double SuccessRate => Executions == 0 ? 0 : (double)Succeeded / Executions;
}

public sealed record MetricsReport
{
    public int Runs { get; init; }
    public int Succeeded { get; init; }
    public int SafeStopped { get; init; }
    public int Rejected { get; init; }
    public int Failed { get; init; }
    public double RunSuccessRate => Runs == 0 ? 0 : (double)Succeeded / Runs;

    public int NodeExecutions { get; init; }
    public int NodeSuccesses { get; init; }
    public double NodeSuccessRate => NodeExecutions == 0 ? 0 : (double)NodeSuccesses / NodeExecutions;

    public int SemanticRetries { get; init; }
    public int LoopBacks { get; init; }
    public int TransientRetries { get; init; }
    public int Fallbacks { get; init; }
    public int ModelFallbacks { get; init; }
    public int CircuitOpens { get; init; }
    public int Rollbacks { get; init; }
    public int Replans { get; init; }
    public int GateFailures { get; init; }
    public int PolicyBlocks { get; init; }
    public double RetriesPerRun => Runs == 0 ? 0 : (double)(SemanticRetries + LoopBacks) / Runs;
    public double RollbackFrequency => Runs == 0 ? 0 : (double)Rollbacks / Runs;

    /// <summary>Mean time from a node's first failure to its next success (recovery by retry/fallback/loop-back/resume).</summary>
    public double? MttrMs { get; init; }
    public int Recoveries { get; init; }

    /// <summary>Wall-clock end-to-end latency per completed run, including time waiting for humans.</summary>
    public double? AvgEndToEndMs { get; init; }
    public double? MaxEndToEndMs { get; init; }
    /// <summary>End-to-end minus human approval wait: what the agents/orchestrator actually spent.</summary>
    public double? AvgAgentTimeMs { get; init; }
    public double HumanWaitMs { get; init; }
    public int Approvals { get; init; }

    public int LlmCalls { get; init; }
    public int LlmLiveCalls { get; init; }
    public int LlmReplayCalls { get; init; }
    public long LlmTokens { get; init; }
    public double? AvgLlmMs { get; init; }

    public IReadOnlyList<StageMetrics> Stages { get; init; } = [];
}

/// <summary>Pure function over the event stream: metrics are never stored separately, so they cannot drift from the audit facts.</summary>
public static class MetricsCalculator
{
    public static MetricsReport Compute(IReadOnlyList<RunEvent> events)
    {
        var byRun = events.GroupBy(e => e.RunId).ToList();
        var finalStatus = byRun
            .Select(g => g.Where(e => e.Type == EventTypes.RunCompleted).OrderBy(e => e.Timestamp).LastOrDefault())
            .Where(e => e is not null)
            .Select(e => e!.Data?.GetValueOrDefault("status") ?? "")
            .ToList();

        // MTTR: per (run,node) first failure after a success/start → next success.
        var recoveries = new List<double>();
        foreach (var g in events.Where(e => e.NodeId is not null).GroupBy(e => (e.RunId, e.NodeId)))
        {
            DateTimeOffset? failingSince = null;
            foreach (var e in g.OrderBy(e => e.Timestamp).ThenBy(e => e.Id))
            {
                if (e.Type is EventTypes.NodeAttemptFailed or EventTypes.GateFailed or EventTypes.NodeFailed or EventTypes.LoopBack)
                    failingSince ??= e.Timestamp;
                else if (e.Type == EventTypes.NodeSucceeded && failingSince is not null)
                {
                    recoveries.Add((e.Timestamp - failingSince.Value).TotalMilliseconds);
                    failingSince = null;
                }
            }
        }

        // End-to-end: sum of active segments (start/resume → completed) so time spent safe-stopped is excluded.
        var e2e = new List<double>();
        var agentTime = new List<double>();
        foreach (var g in byRun)
        {
            var ordered = g.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToList();
            double total = 0;
            DateTimeOffset? segStart = null;
            foreach (var e in ordered)
            {
                if (e.Type is EventTypes.RunStarted or EventTypes.RunResumed) segStart = e.Timestamp;
                else if (e.Type == EventTypes.RunCompleted && segStart is not null)
                {
                    total += (e.Timestamp - segStart.Value).TotalMilliseconds;
                    segStart = null;
                }
            }
            if (!ordered.Any(e => e.Type == EventTypes.RunCompleted)) continue;
            var wait = ordered.Where(e => e.Type == EventTypes.ApprovalResolved).Sum(e => e.DurationMs ?? 0);
            e2e.Add(total);
            agentTime.Add(Math.Max(0, total - wait));
        }

        var stages = events.Where(e => e.Stage is not null)
            .GroupBy(e => e.Stage!)
            .Select(g =>
            {
                var ok = g.Where(e => e.Type == EventTypes.NodeSucceeded).ToList();
                var executions = ok.Count + g.Count(e => e.Type == EventTypes.NodeFailed);
                return new StageMetrics(g.Key, executions, ok.Count,
                    g.Count(e => e.Type is EventTypes.NodeAttemptFailed or EventTypes.GateFailed),
                    ok.Count == 0 ? 0 : ok.Average(e => e.DurationMs ?? 0),
                    ok.Count == 0 ? 0 : ok.Max(e => e.DurationMs ?? 0));
            })
            .Where(s => s.Executions > 0)
            .OrderBy(s => StageOrder(s.Stage))
            .ToList();

        var llm = events.Where(e => e.Type == EventTypes.LlmCall).ToList();
        int Count(string type) => events.Count(e => e.Type == type);

        return new MetricsReport
        {
            Runs = finalStatus.Count,
            Succeeded = finalStatus.Count(s => s == "Succeeded"),
            SafeStopped = finalStatus.Count(s => s == "SafeStopped"),
            Rejected = finalStatus.Count(s => s == "Rejected"),
            Failed = finalStatus.Count(s => s == "Failed"),
            NodeExecutions = Count(EventTypes.NodeSucceeded) + Count(EventTypes.NodeFailed),
            NodeSuccesses = Count(EventTypes.NodeSucceeded),
            SemanticRetries = Count(EventTypes.NodeRetried),
            LoopBacks = Count(EventTypes.LoopBack),
            TransientRetries = Count(EventTypes.LlmTransientRetry),
            Fallbacks = Count(EventTypes.FallbackUsed),
            ModelFallbacks = Count(EventTypes.LlmModelFallback),
            CircuitOpens = Count(EventTypes.CircuitOpened),
            Rollbacks = Count(EventTypes.RolledBack),
            Replans = Count(EventTypes.Replanned),
            GateFailures = Count(EventTypes.GateFailed),
            PolicyBlocks = Count(EventTypes.PolicyBlocked),
            MttrMs = recoveries.Count == 0 ? null : recoveries.Average(),
            Recoveries = recoveries.Count,
            AvgEndToEndMs = e2e.Count == 0 ? null : e2e.Average(),
            MaxEndToEndMs = e2e.Count == 0 ? null : e2e.Max(),
            AvgAgentTimeMs = agentTime.Count == 0 ? null : agentTime.Average(),
            HumanWaitMs = events.Where(e => e.Type == EventTypes.ApprovalResolved).Sum(e => e.DurationMs ?? 0),
            Approvals = Count(EventTypes.ApprovalResolved),
            LlmCalls = llm.Count,
            LlmLiveCalls = llm.Count(e => e.Data?.GetValueOrDefault("source") is "Live" or "FallbackModel"),
            LlmReplayCalls = llm.Count(e => e.Data?.GetValueOrDefault("source") == "Replay"),
            LlmTokens = llm.Sum(e => long.TryParse(e.Data?.GetValueOrDefault("tokens"), out var t) ? t : 0),
            AvgLlmMs = llm.Count == 0 ? null : llm.Average(e => e.DurationMs ?? 0),
            Stages = stages,
        };
    }

    private static int StageOrder(string stage) => stage switch
    {
        "intake" => 0, "workspace" => 1, "requirements" => 2, "architecture" => 3, "planning" => 4,
        "implementation" => 5, "testing" => 6, "documentation" => 7, "validation" => 8, "review" => 9, "release" => 10, _ => 99,
    };
}
