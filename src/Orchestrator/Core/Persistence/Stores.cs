using Orchestrator.Core.Engine;

namespace Orchestrator.Core.Persistence;

/// <summary>Durable run state: enables safe-stop + resume and re-planning across process restarts.</summary>
public interface IRunStore
{
    Task SaveRunAsync(RunRecord run, CancellationToken ct = default);
    Task<RunRecord?> GetRunAsync(string runId, CancellationToken ct = default);
    Task<IReadOnlyList<RunRecord>> ListRunsAsync(int limit = 50, CancellationToken ct = default);
    Task SaveNodeStateAsync(string runId, NodeState state, CancellationToken ct = default);
    Task<IReadOnlyList<NodeState>> GetNodeStatesAsync(string runId, CancellationToken ct = default);
    Task DeleteNodeStateAsync(string runId, string nodeId, CancellationToken ct = default);
    Task SaveArtifactAsync(string runId, Artifact artifact, CancellationToken ct = default);
    /// <summary>Latest version of each artifact key.</summary>
    Task<IReadOnlyList<Artifact>> GetArtifactsAsync(string runId, CancellationToken ct = default);
    /// <summary>Every version (lineage history).</summary>
    Task<IReadOnlyList<Artifact>> GetArtifactHistoryAsync(string runId, CancellationToken ct = default);
}

/// <summary>Append-only operational event stream. Metrics are derived from it.</summary>
public interface IEventStore
{
    Task AppendAsync(RunEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<RunEvent>> GetEventsAsync(string? runId = null, CancellationToken ct = default);
}

/// <summary>Tamper-evident, hash-chained governance log (who did what, who approved, why).</summary>
public interface IAuditLog
{
    Task<AuditEntry> AppendAsync(string runId, string actor, string action, string target, string details, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetEntriesAsync(string? runId = null, CancellationToken ct = default);
    Task<AuditVerification> VerifyAsync(CancellationToken ct = default);
}

public sealed record RunEvent(
    string RunId,
    string Type,
    DateTimeOffset Timestamp,
    string? NodeId = null,
    string? Stage = null,
    double? DurationMs = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public long Id { get; init; }
}

public static class EventTypes
{
    public const string RunStarted = "run.started";
    public const string RunResumed = "run.resumed";
    public const string RunCompleted = "run.completed";
    public const string NodeStarted = "node.started";
    public const string NodeSucceeded = "node.succeeded";
    public const string NodeSkipped = "node.skipped";
    public const string NodeAttemptFailed = "node.attempt_failed";
    public const string GateFailed = "gate.failed";
    public const string NodeRetried = "node.retried";
    public const string NodeFailed = "node.failed";
    public const string FallbackUsed = "node.fallback";
    public const string LoopBack = "node.loop_back";
    public const string Replanned = "plan.replanned";
    public const string Expanded = "plan.expanded";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalResolved = "approval.resolved";
    public const string PolicyBlocked = "policy.blocked";
    public const string RolledBack = "run.rolled_back";
    public const string SafeStopped = "run.safe_stopped";
    public const string LlmCall = "llm.call";
    public const string LlmTransientRetry = "llm.retry";
    public const string LlmModelFallback = "llm.model_fallback";
    public const string CircuitOpened = "llm.circuit_opened";
    public const string CircuitClosed = "llm.circuit_closed";
}

public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string RunId,
    string Actor,
    string Action,
    string Target,
    string Details,
    string PreviousHash,
    string Hash);

public sealed record AuditVerification(bool IsValid, int EntriesChecked, long? BrokenAtSequence, string Message);

public static class AuditChain
{
    public const string Genesis = "GENESIS";

    public static string ComputeHash(long sequence, DateTimeOffset timestamp, string runId, string actor, string action,
        string target, string details, string previousHash) =>
        Common.Hashing.Sha256(string.Join('\u001f', sequence, timestamp.ToUnixTimeMilliseconds(), runId, actor, action, target, details, previousHash));

    public static AuditVerification Verify(IReadOnlyList<AuditEntry> entries)
    {
        var prev = Genesis;
        foreach (var e in entries.OrderBy(e => e.Sequence))
        {
            if (e.PreviousHash != prev)
                return new(false, entries.Count, e.Sequence, $"Entry {e.Sequence}: previous-hash link broken (entry removed or reordered).");
            var expected = ComputeHash(e.Sequence, e.Timestamp, e.RunId, e.Actor, e.Action, e.Target, e.Details, e.PreviousHash);
            if (expected != e.Hash)
                return new(false, entries.Count, e.Sequence, $"Entry {e.Sequence}: content hash mismatch (entry was modified).");
            prev = e.Hash;
        }
        return new(true, entries.Count, null, $"Audit chain intact ({entries.Count} entries).");
    }
}
