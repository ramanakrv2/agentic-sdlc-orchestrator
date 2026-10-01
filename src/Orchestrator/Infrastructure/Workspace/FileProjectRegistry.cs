using System.Text;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Projects;

namespace Orchestrator.Infrastructure.Workspace;

/// <summary>
/// Folder-per-project workspace:
/// <code>
/// workspace/index.json                 ← derived from all cards (one read for routing)
/// workspace/&lt;slug&gt;/project.json       ← card (source of truth)
/// workspace/&lt;slug&gt;/HISTORY.md          ← human-readable version history
/// workspace/&lt;slug&gt;/.versions/&lt;label&gt;/ ← snapshots (approved versions + pre-run checkpoints)
/// workspace/&lt;slug&gt;/.runs/&lt;runId&gt;/     ← per-run evidence
/// </code>
/// </summary>
public sealed class FileProjectRegistry(string workspaceRoot) : IProjectRegistry
{
    private static readonly string[] ExcludedDirs = [".versions", ".runs", "bin", "obj", "TestResults", ".vs"];
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public string WorkspaceRoot { get; } = Path.GetFullPath(workspaceRoot);

    public async Task<IReadOnlyList<ProjectCard>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(WorkspaceRoot)) return [];
        var cards = new List<ProjectCard>();
        foreach (var dir in Directory.EnumerateDirectories(WorkspaceRoot))
        {
            var card = await GetAsync(Path.GetFileName(dir), ct);
            if (card is not null) cards.Add(card);
        }
        return cards;
    }

    public async Task<ProjectCard?> GetAsync(string slug, CancellationToken ct = default)
    {
        var file = Path.Combine(WorkspaceRoot, slug, "project.json");
        return File.Exists(file) ? JsonText.Deserialize<ProjectCard>(await File.ReadAllTextAsync(file, ct)) : null;
    }

    public async Task SaveCardAsync(ProjectCard card, CancellationToken ct = default)
    {
        var dir = Path.Combine(WorkspaceRoot, card.Slug);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "project.json"), JsonText.Serialize(card), ct);
        await RebuildIndexAsync(ct);
    }

    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(WorkspaceRoot);
        var cards = await ListAsync(ct);
        var index = cards.Select(c => new
        {
            c.Slug, c.Name, c.Summary, c.Aliases, c.Capabilities, c.Entities, c.Endpoints, c.CurrentVersion, c.DeploymentProfile,
        });
        await File.WriteAllTextAsync(Path.Combine(WorkspaceRoot, "index.json"), JsonText.Serialize(index), ct);
    }

    public async Task<string> ReserveSlugAsync(string desired, CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            var slug = desired;
            for (var i = 2; Directory.Exists(Path.Combine(WorkspaceRoot, slug)); i++) slug = $"{desired}-{i}";
            Directory.CreateDirectory(Path.Combine(WorkspaceRoot, slug));
            return slug;
        }
        finally { Lock.Release(); }
    }

    public string ComputeSourceHash(string slug)
    {
        var root = Path.Combine(WorkspaceRoot, slug);
        var parts = new List<KeyValuePair<string, string>>();
        foreach (var sub in new[] { "src", "tests" })
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in EnumerateFiles(dir))
                parts.Add(KeyValuePair.Create(Path.GetRelativePath(root, f).Replace('\\', '/'), Hashing.Sha256(File.ReadAllText(f))));
        }
        return Hashing.Fingerprint(parts);
    }

    public Task SnapshotAsync(string slug, string label, CancellationToken ct = default)
    {
        var root = Path.Combine(WorkspaceRoot, slug);
        var target = Path.Combine(root, ".versions", label);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        foreach (var f in EnumerateFiles(root))
        {
            var rel = Path.GetRelativePath(root, f);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
        return Task.CompletedTask;
    }

    public Task<bool> RestoreAsync(string slug, string label, CancellationToken ct = default)
    {
        var root = Path.Combine(WorkspaceRoot, slug);
        var source = Path.Combine(root, ".versions", label);
        if (!Directory.Exists(source)) return Task.FromResult(false);

        foreach (var f in EnumerateFiles(root)) File.Delete(f);
        foreach (var d in Directory.EnumerateDirectories(root).Where(d => !ExcludedDirs.Contains(Path.GetFileName(d))))
            Directory.Delete(d, recursive: true);

        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(root, Path.GetRelativePath(source, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
        return Task.FromResult(true);
    }

    public async Task DeleteProjectAsync(string slug, CancellationToken ct = default)
    {
        var root = Path.Combine(WorkspaceRoot, slug);
        if (Directory.Exists(root))
        {
            // Keep run evidence for the audit trail; remove everything else.
            var runs = Path.Combine(root, ".runs");
            if (Directory.Exists(runs))
            {
                var archive = Path.Combine(WorkspaceRoot, ".archive", $"{slug}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                Directory.Move(runs, archive);
            }
            Directory.Delete(root, recursive: true);
        }
        await RebuildIndexAsync(ct);
    }

    public async Task AppendHistoryAsync(string slug, ProjectVersion v, CancellationToken ct = default)
    {
        var file = Path.Combine(WorkspaceRoot, slug, "HISTORY.md");
        var sb = new StringBuilder();
        if (!File.Exists(file)) sb.AppendLine($"# Version history — {slug}\n\n| Version | Date (UTC) | Type | Requirement | Run | Summary |\n|---|---|---|---|---|---|");
        sb.AppendLine($"| {v.Version} | {v.Date:yyyy-MM-dd HH:mm} | {v.ChangeType} | {Escape(v.Requirement)} | `{v.RunId}` | {Escape(v.Summary)} |");
        await File.AppendAllTextAsync(file, sb.ToString(), ct);
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".db") || name.EndsWith(".db-wal") || name.EndsWith(".db-shm")) continue;
                yield return f;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!ExcludedDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}

/// <summary>Rollback = restore the pre-run checkpoint (brownfield) or remove the half-created project (greenfield).</summary>
public sealed class WorkspaceRollbackService(IProjectRegistry registry) : IRollbackService
{
    public const string CreatedProjectFlag = "workspace.createdProject";
    public const string CheckpointLabelKey = "workspace.checkpoint";

    public async Task<string> RollbackAsync(RunContext run, string reason, CancellationToken ct)
    {
        if (run.ProjectSlug is null) return "No workspace changes to roll back (project not yet selected).";

        if (run.Settings.TryGetValue(CreatedProjectFlag, out var created) && created == "true")
        {
            await registry.DeleteProjectAsync(run.ProjectSlug, ct);
            run.Settings.TryRemove(CreatedProjectFlag, out _);
            return $"Removed partially created project '{run.ProjectSlug}' (run evidence archived).";
        }

        if (run.Settings.TryGetValue(CheckpointLabelKey, out var label) && await registry.RestoreAsync(run.ProjectSlug, label, ct))
            return $"Restored '{run.ProjectSlug}' to checkpoint '{label}'.";

        return $"No checkpoint found for '{run.ProjectSlug}'; nothing restored.";
    }
}
