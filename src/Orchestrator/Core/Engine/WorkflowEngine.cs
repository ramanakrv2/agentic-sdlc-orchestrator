using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orchestrator.Core.Common;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Policy;

namespace Orchestrator.Core.Engine;

/// <summary>
/// Stateful DAG executor with governance.
/// <list type="bullet">
/// <item>Parallel fan-out bounded by <see cref="EngineOptions.MaxDegreeOfParallelism"/>; joins via dependencies/"@group".</item>
/// <item>Per node: entry gates → bounded attempts with feedback → fallback → exit gates → policy/approval → commit.</item>
/// <item>Non-linear flow: validators can loop back to upstream nodes; human "revise" re-runs a node.</item>
/// <item>Re-planning: nodes whose input artifacts changed are invalidated and re-run; planners re-expand the graph.</item>
/// <item>Any stop (failure, rejection, Ctrl+C, handler stop) rolls back the workspace and leaves the run resumable.</item>
/// </list>
/// </summary>
public sealed class WorkflowEngine(
    IRunStore runStore,
    IEventStore eventStore,
    IAuditLog auditLog,
    IApprovalService approvals,
    IRollbackService rollback,
    PolicyEngine policy,
    EngineOptions options,
    TimeProvider time,
    ILogger<WorkflowEngine> logger,
    IEnumerable<IRunObserver> observers)
{
    public static readonly ActivitySource Tracing = new("Orchestrator.Engine");

    private readonly IRunStore runStore = runStore;
    private readonly IEventStore eventStore = eventStore;
    private readonly IAuditLog auditLog = auditLog;
    private readonly IApprovalService approvals = approvals;
    private readonly IRollbackService rollback = rollback;
    private readonly PolicyEngine policy = policy;
    private readonly EngineOptions options = options;
    private readonly TimeProvider time = time;
    private readonly ILogger<WorkflowEngine> logger = logger;
    private readonly IReadOnlyList<IRunObserver> observers = observers.ToList();

    public Task<RunRecord> RunAsync(
        WorkflowGraph graph,
        RunContext context,
        RunRecord record,
        IReadOnlyCollection<NodeState>? existingStates = null,
        CancellationToken ct = default) =>
        new Execution(this, graph, context, record, existingStates).RunAsync(ct);

    private enum OutcomeKind { Succeeded, Failed, Rejected, SafeStop, LoopBack, Interrupted }

    private sealed record Outcome(OutcomeKind Kind, NodeResult? Result = null, string? Error = null, string? Fingerprint = null, double DurationMs = 0);

    private sealed class Execution
    {
        private readonly WorkflowEngine _e;
        private readonly WorkflowGraph _graph;
        private readonly RunContext _ctx;
        private readonly RunRecord _record;
        private readonly Dictionary<string, NodeState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _expandedWith = new(StringComparer.Ordinal);
        private readonly object _budgetLock = new();
        private int _loopBacks;
        private bool _fallbackLoopUsed;
        private RunStatus? _stopStatus;
        private string? _stopReason;

        public Execution(WorkflowEngine engine, WorkflowGraph graph, RunContext ctx, RunRecord record, IReadOnlyCollection<NodeState>? existing)
        {
            _e = engine;
            _graph = graph;
            _ctx = ctx;
            _record = record;
            foreach (var s in existing ?? [])
            {
                // A resumed run re-executes anything that did not finish successfully.
                if (s.Status is NodeStatus.Running or NodeStatus.Failed) s.Status = NodeStatus.Pending;
                _states[s.NodeId] = s;
            }
        }

        public async Task<RunRecord> RunAsync(CancellationToken ct)
        {
            using var activity = Tracing.StartActivity("run", ActivityKind.Internal);
            activity?.SetTag("run.id", _ctx.RunId);
            _graph.Validate();
            foreach (var n in _graph.Nodes) StateOf(n.Id);

            var resumed = _states.Values.Any(s => s.Status != NodeStatus.Pending);
            _record.Status = RunStatus.Running;
            _record.StatusReason = null;
            _record.CompletedAt = null;
            await SaveRecordAsync();
            await EmitAsync(new RunEvent(_ctx.RunId, resumed ? EventTypes.RunResumed : EventTypes.RunStarted, Now));
            await AuditAsync("orchestrator", resumed ? "run.resumed" : "run.started", _ctx.RunId, _ctx.Requirement);

            var running = new Dictionary<Task<Outcome>, string>();
            while (true)
            {
                if (_stopStatus is null && !ct.IsCancellationRequested)
                    await ScheduleAsync(running, ct);

                if (running.Count == 0) break;

                var done = await Task.WhenAny(running.Keys);
                var nodeId = running[done];
                running.Remove(done);
                await HandleOutcomeAsync(_graph[nodeId], await done);
            }

            if (_stopStatus is null && ct.IsCancellationRequested)
                Stop(RunStatus.SafeStopped, "Operator interrupt (Ctrl+C).");

            await CompleteAsync();
            return _record;
        }

        // ---------------------------------------------------------------- scheduling

        private async Task ScheduleAsync(Dictionary<Task<Outcome>, string> running, CancellationToken ct)
        {
            bool progressed;
            do
            {
                progressed = false;
                await ReplanAsync();
                await ExpandAsync();

                var runningIds = running.Values.ToHashSet(StringComparer.Ordinal);
                foreach (var node in _graph.Nodes.Where(n => IsReady(n, runningIds)).ToList())
                {
                    if (running.Count >= _e.options.MaxDegreeOfParallelism) return;

                    if (node.Condition is not null && !node.Condition(_ctx))
                    {
                        var s = StateOf(node.Id);
                        s.Status = NodeStatus.Skipped;
                        s.CompletedAt = Now;
                        await _e.runStore.SaveNodeStateAsync(_ctx.RunId, s, CancellationToken.None);
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.NodeSkipped, Now, node.Id, node.Stage));
                        progressed = true;
                        continue;
                    }

                    var state = StateOf(node.Id);
                    state.Status = NodeStatus.Running;
                    state.StartedAt = Now;
                    await _e.runStore.SaveNodeStateAsync(_ctx.RunId, state, CancellationToken.None);
                    running[Task.Run(() => ExecuteNodeAsync(node, state, ct), CancellationToken.None)] = node.Id;
                    runningIds.Add(node.Id);
                }
            } while (progressed);
        }

        private bool IsReady(NodeDefinition node, HashSet<string> runningIds) =>
            StateOf(node.Id).Status == NodeStatus.Pending
            && !runningIds.Contains(node.Id)
            && _graph.DependenciesOf(node.Id).All(d => StateOf(d).Status is NodeStatus.Succeeded or NodeStatus.Skipped);

        /// <summary>
        /// Build-system style re-planning: a completed node whose input artifacts changed since it ran is stale.
        /// Its consumers become stale in turn once it re-runs and produces different output.
        /// </summary>
        private async Task ReplanAsync()
        {
            foreach (var node in _graph.Nodes.Where(n => n.Inputs.Count > 0).ToList())
            {
                var state = StateOf(node.Id);
                if (state.Status != NodeStatus.Succeeded) continue;
                var fp = Fingerprint(node);
                if (fp == state.InputFingerprint) continue;

                state.Status = NodeStatus.Pending;
                await _e.runStore.SaveNodeStateAsync(_ctx.RunId, state, CancellationToken.None);
                await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.Replanned, Now, node.Id, node.Stage,
                    Data: new Dictionary<string, string> { ["reason"] = "upstream inputs changed", ["inputs"] = string.Join(",", node.Inputs) }));
                await AuditAsync("orchestrator", "plan.replanned", node.Id, $"Inputs changed ({string.Join(", ", node.Inputs)}); node re-queued.");
            }
        }

        /// <summary>Dynamic graph expansion (e.g. planner → task nodes). Re-expands when the planner output changes.</summary>
        private async Task ExpandAsync()
        {
            foreach (var node in _graph.Nodes.Where(n => n.Expander is not null).ToList())
            {
                if (StateOf(node.Id).Status != NodeStatus.Succeeded) continue;
                var outputHash = Hashing.Fingerprint(_ctx.Artifacts.Values.Where(a => a.ProducedBy == node.Id)
                    .Select(a => KeyValuePair.Create(a.Key, a.Hash)));
                if (_expandedWith.TryGetValue(node.Id, out var prev) && prev == outputHash) continue;

                // First expansion in this process keeps persisted child states (resume); a changed plan regenerates every task.
                var stale = _graph.Nodes.Where(n => n.ExpandedBy == node.Id).Select(n => n.Id).ToList();
                foreach (var id in stale)
                {
                    _graph.Remove(id);
                    lock (_states) _states.Remove(id);
                    await _e.runStore.DeleteNodeStateAsync(_ctx.RunId, id, CancellationToken.None);
                }

                var children = node.Expander!(_ctx).ToList();
                _graph.AddRange(children);
                foreach (var c in children) StateOf(c.Id);
                _expandedWith[node.Id] = outputHash;
                await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.Expanded, Now, node.Id, node.Stage,
                    Data: new Dictionary<string, string> { ["nodes"] = string.Join(",", children.Select(c => c.Id)), ["replaced"] = stale.Count.ToString() }));
                await AuditAsync("orchestrator", "plan.expanded", node.Id, $"Added {children.Count} task nodes: {string.Join(", ", children.Select(c => c.Id))}");
            }
        }

        // ---------------------------------------------------------------- node execution

        private async Task<Outcome> ExecuteNodeAsync(NodeDefinition node, NodeState state, CancellationToken ct)
        {
            using var activity = Tracing.StartActivity($"node:{node.Id}");
            activity?.SetTag("node.stage", node.Stage);
            var sw = Stopwatch.StartNew();
            var fingerprint = Fingerprint(node);
            await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.NodeStarted, Now, node.Id, node.Stage));

            try
            {
                var probe = new NodeContext { Run = _ctx, Node = node, Attempt = 0, IsFallback = false };
                foreach (var gate in node.EntryGates)
                {
                    var r = await gate.CheckAsync(probe, null, ct);
                    if (r.Passed) continue;
                    await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.GateFailed, Now, node.Id, node.Stage,
                        Data: new Dictionary<string, string> { ["gate"] = gate.Name, ["kind"] = "entry", ["violations"] = string.Join(" | ", r.Violations) }));
                    return new Outcome(OutcomeKind.Failed, Error: $"Entry gate '{gate.Name}' failed: {string.Join("; ", r.Violations)}");
                }

                var feedback = new List<string>(state.PendingFeedback);
                state.PendingFeedback.Clear();
                var useFallback = state.UseFallback;
                var semanticAttempts = 0;
                var revisions = 0;
                var isRetry = false;
                string? lastError = null;

                while (true)
                {
                    if (semanticAttempts >= node.MaxAttempts)
                    {
                        if (node.Fallback is null || useFallback)
                            return new Outcome(OutcomeKind.Failed, Error: lastError ?? "Attempts exhausted.");
                        useFallback = true;
                        semanticAttempts = node.MaxAttempts - 1; // fallback gets exactly one attempt
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.FallbackUsed, Now, node.Id, node.Stage,
                            Data: new Dictionary<string, string> { ["after"] = lastError ?? "" }));
                        await AuditAsync("orchestrator", "node.fallback", node.Id, $"Primary strategy exhausted; switching to fallback. Last error: {lastError}");
                    }

                    if (isRetry)
                    {
                        if (!TryConsumeRetry())
                            return new Outcome(OutcomeKind.Failed, Error: $"Run retry budget ({_e.options.RetryBudgetPerRun}) exhausted. Last error: {lastError}");
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.NodeRetried, Now, node.Id, node.Stage,
                            Data: new Dictionary<string, string> { ["attempt"] = (state.Attempts + 1).ToString(), ["feedback"] = Truncate(string.Join(" | ", feedback), 500) }));
                    }

                    semanticAttempts++;
                    state.Attempts++;
                    var nodeCtx = new NodeContext { Run = _ctx, Node = node, Attempt = state.Attempts, IsFallback = useFallback, Feedback = feedback.ToList() };

                    NodeResult result;
                    try
                    {
                        result = await (useFallback ? node.Fallback! : node.Handler).ExecuteAsync(nodeCtx, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return new Outcome(OutcomeKind.Interrupted, Error: "Interrupted");
                    }
                    catch (Exception ex)
                    {
                        _e.logger.LogWarning(ex, "Node {NodeId} attempt {Attempt} threw", node.Id, state.Attempts);
                        result = NodeResult.Fail($"{ex.GetType().Name}: {ex.Message}");
                    }

                    if (result.SafeStop) return new Outcome(OutcomeKind.SafeStop, result, result.Error);
                    if (result.RetryTargets is { Count: > 0 }) return new Outcome(OutcomeKind.LoopBack, result, result.Error, fingerprint, sw.Elapsed.TotalMilliseconds);

                    if (!result.Success)
                    {
                        lastError = result.Error;
                        feedback = [result.Error ?? "Unknown failure"];
                        isRetry = true;
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.NodeAttemptFailed, Now, node.Id, node.Stage,
                            Data: new Dictionary<string, string> { ["attempt"] = state.Attempts.ToString(), ["error"] = Truncate(lastError ?? "", 500) }));
                        continue;
                    }

                    var violations = new List<string>();
                    foreach (var gate in node.ExitGates)
                    {
                        var r = await gate.CheckAsync(nodeCtx, result, ct);
                        if (r.Passed) continue;
                        violations.AddRange(r.Violations.Select(v => $"[{gate.Name}] {v}"));
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.GateFailed, Now, node.Id, node.Stage,
                            Data: new Dictionary<string, string> { ["gate"] = gate.Name, ["kind"] = "exit", ["violations"] = Truncate(string.Join(" | ", r.Violations), 800) }));
                    }
                    if (violations.Count > 0)
                    {
                        lastError = string.Join("; ", violations.Take(6));
                        feedback = violations;
                        isRetry = true;
                        continue;
                    }

                    var (verdict, decision) = await GovernAsync(node, result, ct);
                    if (verdict == ApprovalOutcome.Rejected)
                        return new Outcome(OutcomeKind.Rejected, result, decision ?? "Rejected");
                    if (verdict == ApprovalOutcome.Revise)
                    {
                        if (++revisions > _e.options.MaxRevisionsPerNode)
                            return new Outcome(OutcomeKind.Rejected, result, "Too many revision requests.");
                        feedback = [$"Human reviewer requested changes: {decision}"];
                        semanticAttempts--; // human revisions do not consume the semantic retry allowance or budget
                        isRetry = false;
                        continue;
                    }

                    if (node.OnCommit is not null) await node.OnCommit(_ctx, result, ct);
                    return new Outcome(OutcomeKind.Succeeded, result, Fingerprint: fingerprint, DurationMs: sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new Outcome(OutcomeKind.Interrupted, Error: "Interrupted");
            }
            catch (Exception ex)
            {
                _e.logger.LogError(ex, "Node {NodeId} failed unexpectedly", node.Id);
                return new Outcome(OutcomeKind.Failed, Error: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Policy evaluation for the node's declared action plus any runtime-detected high-impact actions.</summary>
        private async Task<(ApprovalOutcome Verdict, string? Comment)> GovernAsync(NodeDefinition node, NodeResult result, CancellationToken ct)
        {
            var actions = new List<string>();
            if (node.Action is not null) actions.Add(node.Action);
            actions.AddRange(result.RequestedActions);

            foreach (var action in actions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                switch (_e.policy.Evaluate(action))
                {
                    case Autonomy.Forbidden:
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.PolicyBlocked, Now, node.Id, node.Stage,
                            Data: new Dictionary<string, string> { ["action"] = action }));
                        await AuditAsync("policy", "action.forbidden", node.Id, $"Action '{action}' is forbidden by policy.");
                        throw new PolicyViolationException($"Action '{action}' is forbidden by policy.");

                    case Autonomy.Notify:
                        await AuditAsync("policy", "action.notify", node.Id, $"Action '{action}' executed autonomously (notify level).");
                        break;

                    case Autonomy.Approve:
                        var request = new ApprovalRequest(_ctx.RunId, node.Id, node.Stage, action, result.Summary ?? $"{node.Id} completed", result.Artifacts);
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.ApprovalRequested, Now, node.Id, node.Stage,
                            Data: new Dictionary<string, string> { ["action"] = action }));
                        var sw = Stopwatch.StartNew();
                        var decision = await _e.approvals.RequestAsync(request, ct);
                        await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.ApprovalResolved, Now, node.Id, node.Stage, sw.Elapsed.TotalMilliseconds,
                            new Dictionary<string, string> { ["action"] = action, ["outcome"] = decision.Outcome.ToString(), ["actor"] = decision.Actor }));
                        await AuditAsync(decision.Actor, $"approval.{decision.Outcome.ToString().ToLowerInvariant()}", node.Id,
                            $"Action '{action}'. {decision.Comment}".Trim());
                        if (decision.Outcome != ApprovalOutcome.Approved) return (decision.Outcome, decision.Comment);
                        break;
                }
            }
            return (ApprovalOutcome.Approved, null);
        }

        // ---------------------------------------------------------------- outcomes

        private async Task HandleOutcomeAsync(NodeDefinition node, Outcome outcome)
        {
            var state = StateOf(node.Id);
            switch (outcome.Kind)
            {
                case OutcomeKind.Succeeded:
                    await CommitAsync(node, state, outcome);
                    break;

                case OutcomeKind.LoopBack:
                    await LoopBackAsync(node, state, outcome);
                    break;

                case OutcomeKind.Interrupted:
                    state.Status = NodeStatus.Pending;
                    await _e.runStore.SaveNodeStateAsync(_ctx.RunId, state, CancellationToken.None);
                    Stop(RunStatus.SafeStopped, "Operator interrupt (Ctrl+C).");
                    break;

                default:
                    state.Status = NodeStatus.Failed;
                    state.LastError = outcome.Error;
                    state.CompletedAt = Now;
                    await _e.runStore.SaveNodeStateAsync(_ctx.RunId, state, CancellationToken.None);
                    await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.NodeFailed, Now, node.Id, node.Stage,
                        Data: new Dictionary<string, string> { ["error"] = Truncate(outcome.Error ?? "", 800), ["kind"] = outcome.Kind.ToString() }));
                    await AuditAsync("orchestrator", "node.failed", node.Id, outcome.Error ?? "");
                    Stop(outcome.Kind == OutcomeKind.Rejected ? RunStatus.Rejected : RunStatus.SafeStopped,
                        $"{node.Id}: {outcome.Error}");
                    break;
            }
        }

        private async Task CommitAsync(NodeDefinition node, NodeState state, Outcome outcome)
        {
            var result = outcome.Result!;
            foreach (var (key, content) in result.Artifacts)
            {
                var existing = _ctx.Artifacts.GetValueOrDefault(key);
                if (existing is not null && existing.Hash == Hashing.Sha256(content)) continue; // unchanged → no downstream invalidation
                var artifact = Artifact.Create(key, content, node.Id, (existing?.Version ?? 0) + 1, Now, outcome.Fingerprint ?? "");
                _ctx.Artifacts[key] = artifact;
                await _e.runStore.SaveArtifactAsync(_ctx.RunId, artifact, CancellationToken.None);
            }

            state.Status = NodeStatus.Succeeded;
            state.InputFingerprint = Fingerprint(node);
            state.LastError = null;
            state.UseFallback = false;
            state.CompletedAt = Now;
            await _e.runStore.SaveNodeStateAsync(_ctx.RunId, state, CancellationToken.None);
            await SaveRecordAsync();

            await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.NodeSucceeded, Now, node.Id, node.Stage, outcome.DurationMs,
                new Dictionary<string, string> { ["attempts"] = state.Attempts.ToString(), ["artifacts"] = string.Join(",", result.Artifacts.Keys) }));
            foreach (var d in result.Decisions)
                await AuditAsync($"agent:{node.Id}", "decision", node.Id, d);
            var lineage = string.Join(", ", result.Artifacts.Keys.Select(k => $"{k}@{_ctx.Artifacts[k].Hash[..10]}"));
            await AuditAsync($"agent:{node.Id}", "node.completed", node.Id, $"inputs={state.InputFingerprint[..10]} outputs=[{lineage}]");
        }

        /// <summary>Validator sent work back upstream (non-linear flow). Bounded; then one fallback pass; then fail.</summary>
        private async Task LoopBackAsync(NodeDefinition node, NodeState state, Outcome outcome)
        {
            var targets = outcome.Result!.RetryTargets!.Where(t => _graph.Contains(t.Key)).ToList();
            var useFallback = false;

            if (_loopBacks >= _e.options.MaxLoopBacks)
            {
                var canFallback = targets.Any(t => _graph[t.Key].Fallback is not null);
                if (_fallbackLoopUsed || !canFallback)
                {
                    await HandleOutcomeAsync(node, outcome with { Kind = OutcomeKind.Failed, Error = $"Validation still failing after {_loopBacks} loop-backs: {outcome.Error}" });
                    return;
                }
                _fallbackLoopUsed = true;
                useFallback = true;
                await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.FallbackUsed, Now, node.Id, node.Stage,
                    Data: new Dictionary<string, string> { ["targets"] = string.Join(",", targets.Select(t => t.Key)), ["scope"] = "loop-back" }));
                await AuditAsync("orchestrator", "loopback.fallback", node.Id, $"Loop-back limit reached; re-running {targets.Count} nodes with fallback strategy.");
            }
            else if (!TryConsumeRetry())
            {
                await HandleOutcomeAsync(node, outcome with { Kind = OutcomeKind.Failed, Error = $"Retry budget exhausted during loop-back: {outcome.Error}" });
                return;
            }

            _loopBacks++;
            foreach (var (targetId, feedback) in targets)
            {
                var ts = StateOf(targetId);
                ts.PendingFeedback = feedback;
                ts.UseFallback = useFallback || ts.UseFallback;
                ts.Status = NodeStatus.Pending;
                await _e.runStore.SaveNodeStateAsync(_ctx.RunId, ts, CancellationToken.None);
            }
            state.Status = NodeStatus.Pending; // the validator itself re-runs once targets complete
            await _e.runStore.SaveNodeStateAsync(_ctx.RunId, state, CancellationToken.None);

            await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.LoopBack, Now, node.Id, node.Stage,
                Data: new Dictionary<string, string> { ["targets"] = string.Join(",", targets.Select(t => t.Key)), ["iteration"] = _loopBacks.ToString(), ["reason"] = Truncate(outcome.Error ?? "", 500) }));
            await AuditAsync("orchestrator", "node.loop_back", node.Id, $"Re-running {string.Join(", ", targets.Select(t => t.Key))}: {Truncate(outcome.Error ?? "", 300)}");
        }

        private void Stop(RunStatus status, string reason)
        {
            if (_stopStatus is not null) return;
            _stopStatus = status;
            _stopReason = reason;
        }

        private async Task CompleteAsync()
        {
            if (_stopStatus is null)
            {
                var incomplete = _graph.Nodes.Where(n => StateOf(n.Id).Status is not (NodeStatus.Succeeded or NodeStatus.Skipped)).Select(n => n.Id).ToList();
                if (incomplete.Count > 0) Stop(RunStatus.SafeStopped, $"Blocked nodes: {string.Join(", ", incomplete)}");
            }

            if (_stopStatus is not null)
            {
                var detail = await _e.rollback.RollbackAsync(_ctx, _stopReason ?? "", CancellationToken.None);
                await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.RolledBack, Now, Data: new Dictionary<string, string> { ["detail"] = detail }));
                await AuditAsync("orchestrator", "run.rolled_back", _ctx.RunId, detail);

                // Workspace was restored, so anything that wrote to it must run again on resume.
                foreach (var n in _graph.Nodes.Where(n => n.Stage is Stages.Workspace or Stages.Implementation or Stages.Testing or Stages.Documentation
                             or Stages.Deployment or Stages.Validation or Stages.Review or Stages.Release))
                {
                    var s = StateOf(n.Id);
                    if (s.Status == NodeStatus.Pending) continue;
                    s.Status = NodeStatus.Pending;
                    await _e.runStore.SaveNodeStateAsync(_ctx.RunId, s, CancellationToken.None);
                }

                await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.SafeStopped, Now, Data: new Dictionary<string, string> { ["reason"] = _stopReason ?? "", ["status"] = _stopStatus.ToString()! }));
                _record.Status = _stopStatus.Value;
                _record.StatusReason = _stopReason;
            }
            else
            {
                _record.Status = RunStatus.Succeeded;
                _record.StatusReason = null;
            }

            _record.CompletedAt = Now;
            await SaveRecordAsync();
            await EmitAsync(new RunEvent(_ctx.RunId, EventTypes.RunCompleted, Now,
                Data: new Dictionary<string, string> { ["status"] = _record.Status.ToString(), ["reason"] = _record.StatusReason ?? "" }));
            await AuditAsync("orchestrator", "run.completed", _ctx.RunId, $"{_record.Status} {_record.StatusReason}".Trim());
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Run-scoped settings (e.g. rollback checkpoint) and project are persisted so a resumed run can still roll back.</summary>
        private Task SaveRecordAsync()
        {
            _record.ProjectSlug = _ctx.ProjectSlug;
            _record.Settings = new Dictionary<string, string>(_ctx.Settings, StringComparer.OrdinalIgnoreCase);
            return _e.runStore.SaveRunAsync(_record, CancellationToken.None);
        }

        private bool TryConsumeRetry()
        {
            lock (_budgetLock)
            {
                if (_record.RetriesUsed >= _e.options.RetryBudgetPerRun) return false;
                _record.RetriesUsed++;
                return true;
            }
        }

        private NodeState StateOf(string id)
        {
            lock (_states)
            {
                if (!_states.TryGetValue(id, out var s)) _states[id] = s = new NodeState { NodeId = id };
                return s;
            }
        }

        private string Fingerprint(NodeDefinition node)
        {
            var parts = new List<KeyValuePair<string, string>>();
            foreach (var input in node.Inputs)
            {
                if (input.EndsWith('*'))
                {
                    var prefix = input[..^1];
                    parts.AddRange(_ctx.Artifacts.Values.Where(a => a.Key.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(a => KeyValuePair.Create(a.Key, a.Hash)));
                }
                else
                {
                    parts.Add(KeyValuePair.Create(input, _ctx.Artifacts.TryGetValue(input, out var a) ? a.Hash : "-"));
                }
            }
            return Hashing.Fingerprint(parts);
        }

        private DateTimeOffset Now => _e.time.GetUtcNow();

        private async Task EmitAsync(RunEvent evt)
        {
            await _e.eventStore.AppendAsync(evt, CancellationToken.None);
            foreach (var o in _e.observers)
            {
                try { o.OnEvent(evt); }
                catch (Exception ex) { _e.logger.LogDebug(ex, "Observer failed"); }
            }
        }

        private Task AuditAsync(string actor, string action, string target, string details) =>
            _e.auditLog.AppendAsync(_ctx.RunId, actor, action, target, details, CancellationToken.None);

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}

public sealed class PolicyViolationException(string message) : Exception(message);

/// <summary>Canonical SDLC stage names.</summary>
public static class Stages
{
    public const string Intake = "intake";
    public const string Workspace = "workspace";
    public const string Requirements = "requirements";
    public const string Architecture = "architecture";
    public const string Planning = "planning";
    public const string Implementation = "implementation";
    public const string Testing = "testing";
    public const string Documentation = "documentation";
    public const string Deployment = "deployment";
    public const string Validation = "validation";
    public const string Review = "review";
    public const string Release = "release";
}
