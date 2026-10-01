using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Llm;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Projects;
using Orchestrator.Infrastructure.Llm;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Workspace;
using Orchestrator.Tests.Support;

namespace Orchestrator.Tests.Infrastructure;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orch-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public sealed class EfStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IDbContextFactory<OrchestratorDbContext> _factory;
    private readonly DbWriteGate _gate = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public EfStoreTests()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite($"Data Source={Path.Combine(_dir.Path, "o.db")}").Options;
        _factory = new PooledDbContextFactory<OrchestratorDbContext>(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Run_state_and_artifact_history_round_trip()
    {
        var store = new EfRunStore(_factory, _gate);
        var now = DateTimeOffset.UtcNow;
        await store.SaveRunAsync(new RunRecord { RunId = "r1", Requirement = "req", StartedAt = now, Settings = new() { ["deploy"] = "local" } }, Ct);
        await store.SaveNodeStateAsync("r1", new NodeState { NodeId = "design", Status = NodeStatus.Succeeded, Attempts = 2, PendingFeedback = ["fix x"] }, Ct);
        await store.SaveArtifactAsync("r1", Artifact.Create("spec", "v1", "analyze", 1, now, "fp1"), Ct);
        await store.SaveArtifactAsync("r1", Artifact.Create("spec", "v2", "analyze", 2, now, "fp2"), Ct);

        (await store.GetRunAsync("r1", Ct))!.Settings["deploy"].ShouldBe("local");
        var state = (await store.GetNodeStatesAsync("r1", Ct)).Single();
        state.Attempts.ShouldBe(2);
        state.PendingFeedback.ShouldBe(["fix x"]);
        (await store.GetArtifactsAsync("r1", Ct)).Single().Content.ShouldBe("v2");
        (await store.GetArtifactHistoryAsync("r1", Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Audit_log_is_hash_chained_and_tampering_is_detected()
    {
        var audit = new EfAuditLog(_factory, _gate, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await audit.AppendAsync("r1", "human:alice", "approval.approved", "release", "ship it", Ct);
        await audit.AppendAsync("r1", "orchestrator", "run.completed", "r1", "Succeeded", Ct);
        (await audit.VerifyAsync(Ct)).IsValid.ShouldBeTrue();

        await using (var db = await _factory.CreateDbContextAsync(Ct))
        {
            var entry = await db.Audit.SingleAsync(a => a.Sequence == 1, Ct);
            entry.Actor = "human:mallory";
            await db.SaveChangesAsync(Ct);
        }

        var v = await audit.VerifyAsync(Ct);
        v.IsValid.ShouldBeFalse();
        v.BrokenAtSequence.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_event_appends_are_serialised_safely()
    {
        var events = new EfEventStore(_factory, _gate);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => events.AppendAsync(new RunEvent("r1", EventTypes.NodeStarted, DateTimeOffset.UtcNow, $"n{i}"), Ct)));
        (await events.GetEventsAsync("r1", Ct)).Count.ShouldBe(50);
    }
}

public sealed class ProjectRegistryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Snapshot_and_restore_roll_back_workspace_changes()
    {
        var registry = new FileProjectRegistry(_dir.Path);
        await registry.SaveCardAsync(new ProjectCard { Slug = "svc", Name = "Svc" }, Ct);
        var file = Path.Combine(_dir.Path, "svc", "src", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "original", Ct);
        var before = registry.ComputeSourceHash("svc");

        await registry.SnapshotAsync("svc", "pre-r1", Ct);
        await File.WriteAllTextAsync(file, "agent change", Ct);
        await File.WriteAllTextAsync(Path.Combine(_dir.Path, "svc", "src", "new.cs"), "new file", Ct);
        registry.ComputeSourceHash("svc").ShouldNotBe(before);

        (await registry.RestoreAsync("svc", "pre-r1", Ct)).ShouldBeTrue();

        (await File.ReadAllTextAsync(file, Ct)).ShouldBe("original");
        File.Exists(Path.Combine(_dir.Path, "svc", "src", "new.cs")).ShouldBeFalse();
        registry.ComputeSourceHash("svc").ShouldBe(before);
    }

    [Fact]
    public async Task Slug_collisions_get_a_suffix_and_index_lists_all_cards()
    {
        var registry = new FileProjectRegistry(_dir.Path);
        (await registry.ReserveSlugAsync("url-shortener", Ct)).ShouldBe("url-shortener");
        (await registry.ReserveSlugAsync("url-shortener", Ct)).ShouldBe("url-shortener-2");
        await registry.SaveCardAsync(new ProjectCard { Slug = "url-shortener", Name = "A" }, Ct);
        File.ReadAllText(Path.Combine(_dir.Path, "index.json")).ShouldContain("url-shortener");
    }

    [Fact]
    public async Task Rollback_of_greenfield_run_removes_the_project_but_keeps_evidence()
    {
        var registry = new FileProjectRegistry(_dir.Path);
        await registry.SaveCardAsync(new ProjectCard { Slug = "svc", Name = "Svc" }, Ct);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "svc", ".runs", "r1"));
        var run = new RunContext { RunId = "r1", Requirement = "x", ProjectSlug = "svc" };
        run.Settings[WorkspaceRollbackService.CreatedProjectFlag] = "true";

        var detail = await new WorkspaceRollbackService(registry).RollbackAsync(run, "failed", Ct);

        detail.ShouldContain("Removed");
        Directory.Exists(Path.Combine(_dir.Path, "svc")).ShouldBeFalse();
        Directory.EnumerateDirectories(Path.Combine(_dir.Path, ".archive")).ShouldNotBeEmpty();
    }
}

public sealed class ResilientLlmClientTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private sealed class FakeModel(Func<string, int, ChatCompletionResult> behaviour) : IChatModel
    {
        public Dictionary<string, int> Calls { get; } = new();

        public Task<ChatCompletionResult> CompleteAsync(string model, LlmRequest request, CancellationToken ct)
        {
            lock (Calls) Calls[model] = Calls.GetValueOrDefault(model) + 1;
            return Task.FromResult(behaviour(model, Calls[model]));
        }
    }

    private (ResilientLlmClient Client, InMemoryEventStore Events, ReplayCache Cache) Create(FakeModel model, LlmMode mode = LlmMode.Auto, int maxRetries = 2, int minThroughput = 2)
    {
        var options = new LlmOptions
        {
            Mode = mode, PrimaryModel = "big", FallbackModel = "small", MaxRetries = maxRetries, TimeoutSeconds = 30,
            CircuitBreaker = new() { MinimumThroughput = minThroughput, FailureRatio = 0.5, SamplingSeconds = 60, BreakSeconds = 60 },
        };
        var events = new InMemoryEventStore();
        var cache = new ReplayCache(_dir.Path);
        return (new ResilientLlmClient(model, cache, options, events, TimeProvider.System, NullLogger<ResilientLlmClient>.Instance), events, cache);
    }

    private static LlmRequest Request(string user = "hello") => new() { Agent = "coder", SystemPrompt = "sys", UserPrompt = user, RunId = "r1" };

    [Fact]
    public async Task Transient_failures_are_retried_then_succeed()
    {
        var model = new FakeModel((_, n) => n < 3 ? throw new HttpRequestException("connection reset") : new ChatCompletionResult("ok", 10, 5, 1));
        var (client, events, _) = Create(model, minThroughput: 10);

        var response = await client.CompleteAsync(Request(), Ct);

        response.Content.ShouldBe("ok");
        response.Source.ShouldBe(LlmSource.Live);
        events.Count(EventTypes.LlmTransientRetry).ShouldBe(2);
    }

    [Fact]
    public async Task Primary_model_failure_falls_back_to_secondary_model()
    {
        var model = new FakeModel((m, _) => m == "big" ? throw new HttpRequestException("503") : new ChatCompletionResult("from small", 1, 1, 1));
        var (client, events, _) = Create(model, maxRetries: 0);

        var response = await client.CompleteAsync(Request(), Ct);

        response.Source.ShouldBe(LlmSource.FallbackModel);
        response.Model.ShouldBe("small");
        events.Count(EventTypes.LlmModelFallback).ShouldBe(1);
    }

    [Fact]
    public async Task Circuit_opens_and_stops_calling_an_unhealthy_model()
    {
        var model = new FakeModel((m, _) => m == "big" ? throw new HttpRequestException("down") : new ChatCompletionResult("ok", 1, 1, 1));
        var (client, events, _) = Create(model, maxRetries: 0);

        for (var i = 0; i < 5; i++) await client.CompleteAsync(Request($"call {i}"), Ct);

        events.Count(EventTypes.CircuitOpened).ShouldBe(1);
        model.Calls["big"].ShouldBe(2); // breaker opened after the minimum throughput; later calls skip "big"
        model.Calls["small"].ShouldBe(5);
    }

    [Fact]
    public async Task All_live_models_down_uses_replay_cache_in_auto_mode()
    {
        var healthy = new FakeModel((_, _) => new ChatCompletionResult("recorded answer", 1, 1, 1));
        var (recorder, _, _) = Create(healthy);
        await recorder.CompleteAsync(Request(), Ct); // records the response

        var down = new FakeModel((_, _) => throw new HttpRequestException("ollama stopped"));
        var (client, events, _) = Create(down, maxRetries: 0);
        var response = await client.CompleteAsync(Request(), Ct);

        response.Source.ShouldBe(LlmSource.Replay);
        response.Content.ShouldBe("recorded answer");
        events.All.ShouldContain(e => e.Type == EventTypes.LlmModelFallback && e.Data!["to"] == "replay");
    }

    [Fact]
    public async Task Replay_mode_miss_saves_the_prompt_and_fails_clearly()
    {
        var (client, _, cache) = Create(new FakeModel((_, _) => throw new InvalidOperationException("must not be called")), LlmMode.Replay);

        var ex = await Should.ThrowAsync<LlmUnavailableException>(() => client.CompleteAsync(Request("unseen prompt"), Ct));

        ex.Message.ShouldContain("No replay entry");
        Directory.GetFiles(Path.Combine(cache.Directory, "pending")).ShouldHaveSingleItem();
    }

    [Fact]
    public void Only_transient_errors_are_retried()
    {
        ResilientLlmClient.IsTransient(new HttpRequestException()).ShouldBeTrue();
        ResilientLlmClient.IsTransient(new Polly.Timeout.TimeoutRejectedException()).ShouldBeTrue();
        ResilientLlmClient.IsTransient(new System.Text.Json.JsonException()).ShouldBeFalse();
        ResilientLlmClient.IsTransient(new ArgumentException()).ShouldBeFalse();
    }
}
