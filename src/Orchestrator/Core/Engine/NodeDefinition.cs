namespace Orchestrator.Core.Engine;

/// <summary>
/// A unit of work in the SDLC graph. Every node has the same contract:
/// entry gates → handler (bounded attempts, optional fallback) → exit gates → policy/approval → commit artifacts.
/// </summary>
public sealed class NodeDefinition
{
    public required string Id { get; init; }

    /// <summary>SDLC stage, used for metrics and display (requirements, design, implementation, testing, ...).</summary>
    public required string Stage { get; init; }

    public required INodeHandler Handler { get; init; }

    /// <summary>Node ids this node waits for. "@group" waits for every node in that group (fan-in / join).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Group membership; a join node can depend on "@group".</summary>
    public string? Group { get; init; }

    /// <summary>Artifact keys this node reads. A change to any of them invalidates the node (dynamic re-planning).</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    public IReadOnlyList<IGate> EntryGates { get; init; } = [];
    public IReadOnlyList<IGate> ExitGates { get; init; } = [];

    /// <summary>Policy action this node performs (e.g. "spec.approve", "release.publish"); policy decides auto/approve/forbidden.</summary>
    public string? Action { get; init; }

    /// <summary>Maximum semantic attempts (handler + exit gates) before fallback/failure.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Alternate strategy used once after attempts are exhausted (e.g. curated replay instead of live model).</summary>
    public INodeHandler? Fallback { get; init; }

    /// <summary>When false the node is skipped (e.g. impact analysis only for brownfield runs).</summary>
    public Func<RunContext, bool>? Condition { get; init; }

    /// <summary>Generates additional nodes from this node's output (e.g. planner → one node per task).</summary>
    public Func<RunContext, IEnumerable<NodeDefinition>>? Expander { get; init; }

    /// <summary>Set on dynamically generated nodes; they are removed and regenerated when the parent is re-planned.</summary>
    public string? ExpandedBy { get; init; }

    /// <summary>
    /// Side effects (e.g. writing files to the workspace) applied only after exit gates and approvals pass,
    /// so rejected or failing output never touches the workspace.
    /// </summary>
    public Func<RunContext, NodeResult, CancellationToken, Task>? OnCommit { get; init; }
}

public interface INodeHandler
{
    Task<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken);
}

/// <summary>Adapter so simple nodes can be declared inline.</summary>
public sealed class DelegateHandler(Func<NodeContext, CancellationToken, Task<NodeResult>> func) : INodeHandler
{
    public Task<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken) => func(context, cancellationToken);
}

public sealed class NodeContext
{
    public required RunContext Run { get; init; }
    public required NodeDefinition Node { get; init; }
    public required int Attempt { get; init; }
    public required bool IsFallback { get; init; }
    /// <summary>Feedback from failed gates, validator loop-backs and human revisions. Agents must address it.</summary>
    public IReadOnlyList<string> Feedback { get; init; } = [];
}

public sealed class NodeResult
{
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    /// <summary>Short human-readable summary shown at approval checkpoints.</summary>
    public string? Summary { get; set; }

    public Dictionary<string, string> Artifacts { get; } = new(StringComparer.Ordinal);

    /// <summary>Decision lineage: rationale recorded in the audit trail.</summary>
    public List<string> Decisions { get; } = [];

    /// <summary>High-impact actions discovered at runtime (e.g. "schema.change"); policy may require approval.</summary>
    public List<string> RequestedActions { get; } = [];

    /// <summary>Non-linear control flow: ask the engine to re-run these upstream nodes with feedback.</summary>
    public Dictionary<string, List<string>>? RetryTargets { get; init; }

    /// <summary>Handler-initiated safe stop (e.g. infeasible requirement and the human chose to abort).</summary>
    public bool SafeStop { get; init; }

    public static NodeResult Ok(string? summary = null) => new() { Summary = summary };
    public static NodeResult Fail(string error) => new() { Success = false, Error = error };
    public static NodeResult Stop(string reason) => new() { Success = false, Error = reason, SafeStop = true };

    public NodeResult With(string key, string content)
    {
        Artifacts[key] = content;
        return this;
    }

    public NodeResult Because(string decision)
    {
        Decisions.Add(decision);
        return this;
    }
}

public sealed record GateResult(bool Passed, IReadOnlyList<string> Violations)
{
    public static readonly GateResult Pass = new(true, []);
    public static GateResult Fail(params string[] violations) => new(false, violations);
    public static GateResult From(IReadOnlyList<string> violations) => violations.Count == 0 ? Pass : new(false, violations);
}

public interface IGate
{
    string Name { get; }

    /// <param name="result">Null for entry gates; the candidate output for exit gates.</param>
    Task<GateResult> CheckAsync(NodeContext context, NodeResult? result, CancellationToken cancellationToken);
}

public sealed class Gate(string name, Func<NodeContext, NodeResult?, CancellationToken, Task<GateResult>> check) : IGate
{
    public string Name => name;
    public Task<GateResult> CheckAsync(NodeContext context, NodeResult? result, CancellationToken cancellationToken) =>
        check(context, result, cancellationToken);

    public static Gate Sync(string name, Func<NodeContext, NodeResult?, GateResult> check) =>
        new(name, (c, r, _) => Task.FromResult(check(c, r)));

    /// <summary>Entry gate: required artifacts must exist.</summary>
    public static Gate RequiresArtifacts(params string[] keys) => Sync($"requires:{string.Join(',', keys)}", (c, _) =>
        GateResult.From(keys.Where(k => c.Run.Get(k) is null).Select(k => $"Missing input artifact '{k}'").ToList()));

    /// <summary>Exit gate: node must produce the declared artifacts, non-empty.</summary>
    public static Gate Produces(params string[] keys) => Sync($"produces:{string.Join(',', keys)}", (_, r) =>
        GateResult.From(keys.Where(k => r is null || !r.Artifacts.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v))
            .Select(k => $"Output artifact '{k}' was not produced").ToList()));
}
