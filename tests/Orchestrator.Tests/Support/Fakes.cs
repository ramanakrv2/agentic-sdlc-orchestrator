using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Policy;

namespace Orchestrator.Tests.Support;

public sealed class InMemoryRunStore : IRunStore
{
    public ConcurrentDictionary<string, RunRecord> Runs { get; } = new();
    public ConcurrentDictionary<(string, string), NodeState> States { get; } = new();
    public ConcurrentBag<(string RunId, Artifact Artifact)> Artifacts { get; } = [];

    public Task SaveRunAsync(RunRecord run, CancellationToken ct = default) { Runs[run.RunId] = run; return Task.CompletedTask; }
    public Task<RunRecord?> GetRunAsync(string runId, CancellationToken ct = default) => Task.FromResult(Runs.GetValueOrDefault(runId));
    public Task<IReadOnlyList<RunRecord>> ListRunsAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RunRecord>>(Runs.Values.ToList());

    public Task SaveNodeStateAsync(string runId, NodeState state, CancellationToken ct = default)
    {
        States[(runId, state.NodeId)] = Clone(state);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeState>> GetNodeStatesAsync(string runId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NodeState>>(States.Where(kv => kv.Key.Item1 == runId).Select(kv => Clone(kv.Value)).ToList());

    public Task DeleteNodeStateAsync(string runId, string nodeId, CancellationToken ct = default) { States.TryRemove((runId, nodeId), out _); return Task.CompletedTask; }
    public Task SaveArtifactAsync(string runId, Artifact artifact, CancellationToken ct = default) { Artifacts.Add((runId, artifact)); return Task.CompletedTask; }

    public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(string runId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Artifact>>(Artifacts.Where(a => a.RunId == runId).GroupBy(a => a.Artifact.Key).Select(g => g.MaxBy(a => a.Artifact.Version).Artifact).ToList());

    public Task<IReadOnlyList<Artifact>> GetArtifactHistoryAsync(string runId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Artifact>>(Artifacts.Where(a => a.RunId == runId).Select(a => a.Artifact).ToList());

    private static NodeState Clone(NodeState s) => new()
    {
        NodeId = s.NodeId, Status = s.Status, Attempts = s.Attempts, InputFingerprint = s.InputFingerprint, LastError = s.LastError,
        StartedAt = s.StartedAt, CompletedAt = s.CompletedAt, PendingFeedback = [.. s.PendingFeedback], UseFallback = s.UseFallback,
    };
}

public sealed class InMemoryEventStore : IEventStore
{
    private long _id;
    public ConcurrentQueue<RunEvent> All { get; } = new();

    public Task AppendAsync(RunEvent evt, CancellationToken ct = default)
    {
        All.Enqueue(evt with { Id = Interlocked.Increment(ref _id) });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunEvent>> GetEventsAsync(string? runId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunEvent>>(All.Where(e => runId is null || e.RunId == runId).ToList());

    public int Count(string type, string? nodeId = null) => All.Count(e => e.Type == type && (nodeId is null || e.NodeId == nodeId));
}

public sealed class InMemoryAuditLog(TimeProvider time) : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public List<AuditEntry> Entries => _entries;

    public Task<AuditEntry> AppendAsync(string runId, string actor, string action, string target, string details, CancellationToken ct = default)
    {
        lock (_entries)
        {
            var seq = _entries.Count + 1;
            var prev = _entries.LastOrDefault()?.Hash ?? AuditChain.Genesis;
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
            var entry = new AuditEntry(seq, ts, runId, actor, action, target, details, prev, AuditChain.ComputeHash(seq, ts, runId, actor, action, target, details, prev));
            _entries.Add(entry);
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<AuditEntry>> GetEntriesAsync(string? runId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(_entries.Where(e => runId is null || e.RunId == runId).ToList());

    public Task<AuditVerification> VerifyAsync(CancellationToken ct = default) => Task.FromResult(AuditChain.Verify(_entries));
}

public sealed class ScriptedApprovals : IApprovalService
{
    private readonly Queue<ApprovalDecision> _decisions = new();
    public List<ApprovalRequest> Requests { get; } = [];

    public ScriptedApprovals Then(ApprovalDecision d) { _decisions.Enqueue(d); return this; }

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : ApprovalDecision.Approve("test"));
    }
}

public sealed class RecordingRollback : IRollbackService
{
    public List<string> Reasons { get; } = [];
    public Task<string> RollbackAsync(RunContext run, string reason, CancellationToken ct) { Reasons.Add(reason); return Task.FromResult("rolled back (test)"); }
}

/// <summary>Builds an engine wired to in-memory fakes.</summary>
public sealed class EngineHarness
{
    public InMemoryRunStore Store { get; } = new();
    public InMemoryEventStore Events { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public InMemoryAuditLog Audit { get; }
    public ScriptedApprovals Approvals { get; } = new();
    public RecordingRollback Rollback { get; } = new();
    public EngineOptions Options { get; } = new() { MaxDegreeOfParallelism = 4, RetryBudgetPerRun = 10, MaxLoopBacks = 2 };
    public PolicyDocument Policy { get; } = new();

    public EngineHarness() => Audit = new InMemoryAuditLog(Time);

    public WorkflowEngine Engine() =>
        new(Store, Events, Audit, Approvals, Rollback, new PolicyEngine(Policy), Options, Time, NullLogger<WorkflowEngine>.Instance, []);

    public (RunContext Ctx, RunRecord Record) NewRun(string runId = "r1")
    {
        var ctx = new RunContext { RunId = runId, Requirement = "test requirement" };
        return (ctx, new RunRecord { RunId = runId, Requirement = ctx.Requirement, StartedAt = Time.GetUtcNow() });
    }

    public Task<RunRecord> RunAsync(WorkflowGraph graph, RunContext ctx, RunRecord record, IReadOnlyCollection<NodeState>? states = null, CancellationToken ct = default) =>
        Engine().RunAsync(graph, ctx, record, states, ct);
}

public static class Nodes
{
    public static NodeDefinition Node(string id, Func<NodeContext, NodeResult> handler, params string[] dependsOn) => new()
    {
        Id = id, Stage = "test", DependsOn = dependsOn,
        Handler = new DelegateHandler((ctx, _) => Task.FromResult(handler(ctx))),
    };

    public static NodeDefinition Produces(string id, string key, string value, params string[] dependsOn) =>
        Node(id, _ => NodeResult.Ok().With(key, value), dependsOn);
}
