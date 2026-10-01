namespace Orchestrator.Core.Engine;

public enum ApprovalOutcome { Approved, Rejected, Revise }

public sealed record ApprovalRequest(
    string RunId,
    string NodeId,
    string Stage,
    string Action,
    string Summary,
    IReadOnlyDictionary<string, string> Artifacts);

public sealed record ApprovalDecision(ApprovalOutcome Outcome, string Actor, string? Comment = null)
{
    public static ApprovalDecision Approve(string actor, string? comment = null) => new(ApprovalOutcome.Approved, actor, comment);
    public static ApprovalDecision Reject(string actor, string? comment = null) => new(ApprovalOutcome.Rejected, actor, comment);
    public static ApprovalDecision Revise(string actor, string comment) => new(ApprovalOutcome.Revise, actor, comment);
}

/// <summary>Human approval checkpoint. CLI implementation prompts; tests and --yes use automatic implementations.</summary>
public interface IApprovalService
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>Human-in-the-loop questions (clarifications, deployment target, routing confirmation).</summary>
public interface IHumanInteraction
{
    /// <returns>The chosen option (one of <paramref name="options"/>) or free text when options is empty.</returns>
    Task<string> AskAsync(string runId, string questionId, string question, IReadOnlyList<string> options, string? defaultAnswer, CancellationToken ct);

    Task NotifyAsync(string runId, string message, CancellationToken ct);
}

/// <summary>Restores the workspace to the pre-run checkpoint when a run fails or is rejected.</summary>
public interface IRollbackService
{
    Task<string> RollbackAsync(RunContext run, string reason, CancellationToken ct);
}

/// <summary>Live progress output (console). Persistence is handled separately by the event store.</summary>
public interface IRunObserver
{
    void OnEvent(Persistence.RunEvent evt);
}

public sealed class EngineOptions
{
    public int MaxDegreeOfParallelism { get; set; } = 4;
    /// <summary>Total semantic retries allowed per run across all nodes (prevents retry storms).</summary>
    public int RetryBudgetPerRun { get; set; } = 10;
    /// <summary>How many times a validator may send work back upstream before fallback/failure.</summary>
    public int MaxLoopBacks { get; set; } = 2;
    /// <summary>Human "revise" requests per node before it is treated as rejected.</summary>
    public int MaxRevisionsPerNode { get; set; } = 3;
}
