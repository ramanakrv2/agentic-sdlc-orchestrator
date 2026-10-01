using System.Collections.Concurrent;
using Orchestrator.Core.Common;

namespace Orchestrator.Core.Engine;

public enum NodeStatus { Pending, Running, Succeeded, Failed, Skipped }

public enum RunStatus { Running, Succeeded, Failed, SafeStopped, Rejected }

/// <summary>An immutable, content-addressed output of a node. Lineage = who produced it and from which inputs.</summary>
public sealed record Artifact(
    string Key,
    string Content,
    string Hash,
    string ProducedBy,
    int Version,
    DateTimeOffset CreatedAt,
    string InputFingerprint)
{
    public static Artifact Create(string key, string content, string producedBy, int version, DateTimeOffset at, string inputFingerprint) =>
        new(key, content, Hashing.Sha256(content), producedBy, version, at, inputFingerprint);
}

/// <summary>Persistent execution state of one node within a run.</summary>
public sealed class NodeState
{
    public required string NodeId { get; init; }
    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public int Attempts { get; set; }
    public string? InputFingerprint { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Feedback queued for the next execution (gate violations, validator loop-back, human revision notes).</summary>
    public List<string> PendingFeedback { get; set; } = [];
    public bool UseFallback { get; set; }
}

/// <summary>Cross-stage shared context. Agents communicate only through artifacts, which gives lineage for free.</summary>
public sealed class RunContext
{
    public required string RunId { get; init; }
    public required string Requirement { get; init; }
    public string? ProjectSlug { get; set; }
    public string WorkspaceRoot { get; init; } = "workspace";
    public ConcurrentDictionary<string, Artifact> Artifacts { get; } = new(StringComparer.Ordinal);
    /// <summary>Run-scoped settings (CLI flags, deployment answers, scenario answers).</summary>
    public ConcurrentDictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Artifacts.TryGetValue(key, out var a) ? a.Content : null;

    public string Require(string key) =>
        Get(key) ?? throw new InvalidOperationException($"Required artifact '{key}' is missing.");

    public string? ProjectPath => ProjectSlug is null ? null : Path.Combine(WorkspaceRoot, ProjectSlug);
}

public sealed class RunRecord
{
    public required string RunId { get; init; }
    public required string Requirement { get; init; }
    public string? ProjectSlug { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;
    public string? StatusReason { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int RetriesUsed { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
