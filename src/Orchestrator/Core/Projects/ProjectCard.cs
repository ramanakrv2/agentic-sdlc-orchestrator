namespace Orchestrator.Core.Projects;

/// <summary>
/// Summary card stored as workspace/&lt;slug&gt;/project.json. Routing reads only cards (via workspace/index.json),
/// never source code, so brownfield matching stays fast and cheap.
/// </summary>
public sealed class ProjectCard
{
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> Capabilities { get; set; } = [];
    public List<string> Entities { get; set; } = [];
    /// <summary>Extracted from code (MapGet/MapPost...), not from the LLM.</summary>
    public List<string> Endpoints { get; set; } = [];
    /// <summary>File → responsibility. Used by impact analysis to narrow which files to read.</summary>
    public Dictionary<string, string> ModuleMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DeploymentProfile { get; set; } = "local";
    public string CurrentVersion { get; set; } = "v0";
    public List<ProjectVersion> Versions { get; set; } = [];
    /// <summary>Hash of src/ at the time the card was written; mismatch = card is stale.</summary>
    public string SourceHash { get; set; } = "";

    public int NextVersionNumber => Versions.Count + 1;
}

public sealed record ProjectVersion(string Version, string ChangeType, string Requirement, string RunId, DateTimeOffset Date, string Summary);

public interface IProjectRegistry
{
    string WorkspaceRoot { get; }
    Task<IReadOnlyList<ProjectCard>> ListAsync(CancellationToken ct = default);
    Task<ProjectCard?> GetAsync(string slug, CancellationToken ct = default);
    Task SaveCardAsync(ProjectCard card, CancellationToken ct = default);
    Task RebuildIndexAsync(CancellationToken ct = default);
    /// <summary>Unique slug: appends -2, -3… when the name collides with a different project.</summary>
    Task<string> ReserveSlugAsync(string desired, CancellationToken ct = default);
    /// <summary>Hash of the project's source tree (detects edits made outside the orchestrator).</summary>
    string ComputeSourceHash(string slug);
    Task SnapshotAsync(string slug, string label, CancellationToken ct = default);
    Task<bool> RestoreAsync(string slug, string label, CancellationToken ct = default);
    Task DeleteProjectAsync(string slug, CancellationToken ct = default);
    Task AppendHistoryAsync(string slug, ProjectVersion version, CancellationToken ct = default);
}
