using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Persistence;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>
/// Serialises writes. SQLite is single-writer; parallel nodes write state concurrently, so one gate per process
/// avoids SQLITE_BUSY. A server database (SQL Server/Postgres) would not need it but it is harmless there.
/// </summary>
public sealed class DbWriteGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await Semaphore.WaitAsync(ct);
        try { return await action(); }
        finally { Semaphore.Release(); }
    }

    public Task RunAsync(Func<Task> action, CancellationToken ct) =>
        RunAsync(async () => { await action(); return true; }, ct);
}

public sealed class EfRunStore(IDbContextFactory<OrchestratorDbContext> factory, DbWriteGate gate) : IRunStore
{
    public Task SaveRunAsync(RunRecord run, CancellationToken ct = default) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.Runs.FindAsync([run.RunId], ct);
        if (e is null) db.Runs.Add(e = new RunEntity { RunId = run.RunId });
        e.Requirement = run.Requirement;
        e.ProjectSlug = run.ProjectSlug;
        e.Status = run.Status.ToString();
        e.StatusReason = run.StatusReason;
        e.StartedAt = run.StartedAt;
        e.CompletedAt = run.CompletedAt;
        e.RetriesUsed = run.RetriesUsed;
        e.SettingsJson = JsonSerializer.Serialize(run.Settings);
        await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<RunRecord?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct);
        return e is null ? null : Map(e);
    }

    public async Task<IReadOnlyList<RunRecord>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Runs.AsNoTracking().ToListAsync(ct);
        return list.OrderByDescending(r => r.StartedAt).Take(limit).Select(Map).ToList();
    }

    public Task SaveNodeStateAsync(string runId, NodeState s, CancellationToken ct = default) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.NodeStates.FindAsync([runId, s.NodeId], ct);
        if (e is null) db.NodeStates.Add(e = new NodeStateEntity { RunId = runId, NodeId = s.NodeId });
        e.Status = s.Status.ToString();
        e.Attempts = s.Attempts;
        e.InputFingerprint = s.InputFingerprint;
        e.LastError = s.LastError;
        e.StartedAt = s.StartedAt;
        e.CompletedAt = s.CompletedAt;
        e.PendingFeedbackJson = JsonSerializer.Serialize(s.PendingFeedback);
        e.UseFallback = s.UseFallback;
        await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<IReadOnlyList<NodeState>> GetNodeStatesAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.NodeStates.AsNoTracking().Where(n => n.RunId == runId).ToListAsync(ct))
            .Select(e => new NodeState
            {
                NodeId = e.NodeId,
                Status = Enum.Parse<NodeStatus>(e.Status),
                Attempts = e.Attempts,
                InputFingerprint = e.InputFingerprint,
                LastError = e.LastError,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                PendingFeedback = JsonSerializer.Deserialize<List<string>>(e.PendingFeedbackJson) ?? [],
                UseFallback = e.UseFallback,
            }).ToList();
    }

    public Task DeleteNodeStateAsync(string runId, string nodeId, CancellationToken ct = default) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.NodeStates.Where(n => n.RunId == runId && n.NodeId == nodeId).ExecuteDeleteAsync(ct);
    }, ct);

    public Task SaveArtifactAsync(string runId, Artifact a, CancellationToken ct = default) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Artifacts.AnyAsync(x => x.RunId == runId && x.Key == a.Key && x.Version == a.Version, ct)) return;
        db.Artifacts.Add(new ArtifactEntity
        {
            RunId = runId, Key = a.Key, Version = a.Version, Content = a.Content, Hash = a.Hash,
            ProducedBy = a.ProducedBy, InputFingerprint = a.InputFingerprint, CreatedAt = a.CreatedAt,
        });
        await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<IReadOnlyList<Artifact>> GetArtifactsAsync(string runId, CancellationToken ct = default) =>
        (await GetArtifactHistoryAsync(runId, ct)).GroupBy(a => a.Key).Select(g => g.MaxBy(a => a.Version)!).ToList();

    public async Task<IReadOnlyList<Artifact>> GetArtifactHistoryAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Artifacts.AsNoTracking().Where(a => a.RunId == runId).OrderBy(a => a.Id).ToListAsync(ct))
            .Select(e => new Artifact(e.Key, e.Content, e.Hash, e.ProducedBy, e.Version, e.CreatedAt, e.InputFingerprint))
            .ToList();
    }

    private static RunRecord Map(RunEntity e) => new()
    {
        RunId = e.RunId,
        Requirement = e.Requirement,
        ProjectSlug = e.ProjectSlug,
        Status = Enum.Parse<RunStatus>(e.Status),
        StatusReason = e.StatusReason,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        RetriesUsed = e.RetriesUsed,
        Settings = JsonSerializer.Deserialize<Dictionary<string, string>>(e.SettingsJson) ?? [],
    };
}

public sealed class EfEventStore(IDbContextFactory<OrchestratorDbContext> factory, DbWriteGate gate) : IEventStore
{
    public Task AppendAsync(RunEvent evt, CancellationToken ct = default) => gate.RunAsync(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Events.Add(new EventEntity
        {
            RunId = evt.RunId, Type = evt.Type, Timestamp = evt.Timestamp, NodeId = evt.NodeId, Stage = evt.Stage,
            DurationMs = evt.DurationMs, DataJson = evt.Data is null ? null : JsonSerializer.Serialize(evt.Data),
        });
        await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<IReadOnlyList<RunEvent>> GetEventsAsync(string? runId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Events.AsNoTracking();
        if (runId is not null) q = q.Where(e => e.RunId == runId);
        return (await q.OrderBy(e => e.Id).ToListAsync(ct))
            .Select(e => new RunEvent(e.RunId, e.Type, e.Timestamp, e.NodeId, e.Stage, e.DurationMs,
                e.DataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(e.DataJson)) { Id = e.Id })
            .ToList();
    }
}

public sealed class EfAuditLog(IDbContextFactory<OrchestratorDbContext> factory, DbWriteGate gate, TimeProvider time) : IAuditLog
{
    public Task<AuditEntry> AppendAsync(string runId, string actor, string action, string target, string details, CancellationToken ct = default) =>
        gate.RunAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var last = await db.Audit.OrderByDescending(a => a.Sequence).FirstOrDefaultAsync(ct);
            var seq = (last?.Sequence ?? 0) + 1;
            var prev = last?.Hash ?? AuditChain.Genesis;
            // Millisecond precision so the hash survives a database round-trip.
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
            var hash = AuditChain.ComputeHash(seq, ts, runId, actor, action, target, details, prev);
            var entity = new AuditEntity
            {
                Sequence = seq, Timestamp = ts, RunId = runId, Actor = actor, Action = action,
                Target = target, Details = details, PreviousHash = prev, Hash = hash,
            };
            db.Audit.Add(entity);
            await db.SaveChangesAsync(ct);
            return Map(entity);
        }, ct);

    public async Task<IReadOnlyList<AuditEntry>> GetEntriesAsync(string? runId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Audit.AsNoTracking();
        if (runId is not null) q = q.Where(a => a.RunId == runId);
        return (await q.OrderBy(a => a.Sequence).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken ct = default) =>
        AuditChain.Verify(await GetEntriesAsync(null, ct));

    private static AuditEntry Map(AuditEntity e) =>
        new(e.Sequence, e.Timestamp, e.RunId, e.Actor, e.Action, e.Target, e.Details, e.PreviousHash, e.Hash);
}
