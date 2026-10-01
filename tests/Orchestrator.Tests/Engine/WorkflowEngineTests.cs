using Orchestrator.Core.Engine;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Policy;
using Orchestrator.Tests.Support;
using static Orchestrator.Tests.Support.Nodes;

namespace Orchestrator.Tests.Engine;

public sealed class WorkflowEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Executes_in_dependency_order_and_shares_context_through_artifacts()
    {
        var h = new EngineHarness();
        var order = new List<string>();
        var g = new WorkflowGraph()
            .Add(Node("a", _ => { lock (order) order.Add("a"); return NodeResult.Ok().With("x", "1"); }))
            .Add(Node("b", c => { lock (order) order.Add("b"); return NodeResult.Ok().With("y", c.Run.Require("x") + "2"); }, "a"));
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        order.ShouldBe(["a", "b"]);
        ctx.Get("y").ShouldBe("12");
        ctx.Artifacts["y"].ProducedBy.ShouldBe("b");
    }

    [Fact]
    public async Task Parallel_branches_run_concurrently_and_join_waits_for_all()
    {
        var h = new EngineHarness();
        var barrier = new Barrier(2);
        NodeResult Rendezvous(NodeContext _) =>
            barrier.SignalAndWait(TimeSpan.FromSeconds(5)) ? NodeResult.Ok() : NodeResult.Fail("branches did not overlap");
        var g = new WorkflowGraph()
            .Add(Node("start", _ => NodeResult.Ok()))
            .Add(Node("left", Rendezvous, "start"))
            .Add(Node("right", Rendezvous, "start"))
            .Add(Node("join", _ => NodeResult.Ok(), "left", "right"));
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        var started = h.Events.All.Where(e => e.Type == EventTypes.NodeStarted).Select(e => e.NodeId).ToList();
        started.Last().ShouldBe("join");
    }

    [Fact]
    public async Task Failed_entry_gate_blocks_node_and_safe_stops_with_rollback()
    {
        var h = new EngineHarness();
        var ran = false;
        var node = Node("needs-input", _ => { ran = true; return NodeResult.Ok(); });
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = node.Id, Stage = node.Stage, Handler = node.Handler, EntryGates = [Gate.RequiresArtifacts("missing")],
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        ran.ShouldBeFalse();
        result.Status.ShouldBe(RunStatus.SafeStopped);
        h.Rollback.Reasons.ShouldHaveSingleItem();
        h.Events.Count(EventTypes.GateFailed).ShouldBe(1);
    }

    [Fact]
    public async Task Exit_gate_failure_retries_with_feedback_until_it_passes()
    {
        var h = new EngineHarness();
        var feedbackSeen = new List<string>();
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = "writer", Stage = "test", MaxAttempts = 3,
            Handler = new DelegateHandler((c, _) =>
            {
                feedbackSeen.AddRange(c.Feedback);
                return Task.FromResult(NodeResult.Ok().With("out", c.Attempt >= 2 ? "good" : "bad"));
            }),
            ExitGates = [Gate.Sync("must-be-good", (_, r) => r!.Artifacts["out"] == "good" ? GateResult.Pass : GateResult.Fail("output was bad"))],
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        feedbackSeen.ShouldContain(f => f.Contains("output was bad"));
        h.Events.Count(EventTypes.NodeRetried).ShouldBe(1);
        rec.RetriesUsed.ShouldBe(1);
    }

    [Fact]
    public async Task Exhausted_attempts_switch_to_fallback_strategy()
    {
        var h = new EngineHarness();
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = "flaky", Stage = "test", MaxAttempts = 2,
            Handler = new DelegateHandler((_, _) => Task.FromResult(NodeResult.Fail("primary broken"))),
            Fallback = new DelegateHandler((c, _) => Task.FromResult(c.IsFallback ? NodeResult.Ok().With("out", "from-fallback") : NodeResult.Fail("?"))),
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        ctx.Get("out").ShouldBe("from-fallback");
        h.Events.Count(EventTypes.FallbackUsed).ShouldBe(1);
    }

    [Fact]
    public async Task Retry_budget_is_enforced_across_the_run()
    {
        var h = new EngineHarness();
        h.Options.RetryBudgetPerRun = 2;
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = "always-fails", Stage = "test", MaxAttempts = 10,
            Handler = new DelegateHandler((_, _) => Task.FromResult(NodeResult.Fail("nope"))),
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.SafeStopped);
        result.StatusReason.ShouldContain("retry budget");
        h.Events.Count(EventTypes.NodeRetried).ShouldBe(2);
    }

    [Fact]
    public async Task Approval_rejection_rolls_back_and_marks_run_rejected()
    {
        var h = new EngineHarness();
        h.Policy.Autonomy.Actions["release.publish"] = Autonomy.Approve;
        h.Approvals.Then(ApprovalDecision.Reject("human:test", "not ready"));
        var g = new WorkflowGraph().Add(new NodeDefinition { Id = "release", Stage = "release", Action = "release.publish", Handler = new DelegateHandler((_, _) => Task.FromResult(NodeResult.Ok("publish v1"))) });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Rejected);
        h.Approvals.Requests.ShouldHaveSingleItem().Summary.ShouldBe("publish v1");
        h.Rollback.Reasons.ShouldHaveSingleItem();
        h.Audit.Entries.ShouldContain(e => e.Action == "approval.rejected" && e.Actor == "human:test");
    }

    [Fact]
    public async Task Approval_revise_reruns_node_with_human_feedback_without_spending_budget()
    {
        var h = new EngineHarness();
        h.Policy.Autonomy.Actions["spec.approve"] = Autonomy.Approve;
        h.Approvals.Then(ApprovalDecision.Revise("human:test", "add expiry")).Then(ApprovalDecision.Approve("human:test"));
        var versions = new List<string>();
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = "spec", Stage = "requirements", Action = "spec.approve",
            Handler = new DelegateHandler((c, _) =>
            {
                var v = c.Feedback.Any(f => f.Contains("add expiry")) ? "with expiry" : "basic";
                versions.Add(v);
                return Task.FromResult(NodeResult.Ok().With("spec", v));
            }),
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        versions.ShouldBe(["basic", "with expiry"]);
        ctx.Get("spec").ShouldBe("with expiry");
        rec.RetriesUsed.ShouldBe(0);
    }

    [Fact]
    public async Task Forbidden_action_is_blocked_by_policy()
    {
        var h = new EngineHarness();
        h.Policy.Autonomy.Actions["secrets.write"] = Autonomy.Forbidden;
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = "sneaky", Stage = "implementation", MaxAttempts = 1,
            Handler = new DelegateHandler((_, _) => { var r = NodeResult.Ok(); r.RequestedActions.Add("secrets.write"); return Task.FromResult(r); }),
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.SafeStopped);
        h.Events.Count(EventTypes.PolicyBlocked).ShouldBe(1);
        h.Audit.Entries.ShouldContain(e => e.Action == "action.forbidden");
    }

    [Fact]
    public async Task Validator_loop_back_reruns_upstream_task_with_feedback()
    {
        var h = new EngineHarness();
        var coderRuns = 0;
        var validatorRuns = 0;
        var g = new WorkflowGraph()
            .Add(Node("code", c => { coderRuns++; return NodeResult.Ok().With("file:a.cs", c.Feedback.Count > 0 ? "fixed" : "broken"); }))
            .Add(new NodeDefinition
            {
                Id = "validate", Stage = Stages.Validation, DependsOn = ["code"], Inputs = ["file:*"],
                Handler = new DelegateHandler((c, _) =>
                {
                    validatorRuns++;
                    return Task.FromResult(c.Run.Get("file:a.cs") == "fixed"
                        ? NodeResult.Ok()
                        : new NodeResult { Success = false, Error = "CS1002 ; expected", RetryTargets = new() { ["code"] = ["a.cs(3,1): CS1002 ; expected"] } });
                }),
            });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        coderRuns.ShouldBe(2);
        validatorRuns.ShouldBe(2);
        h.Events.Count(EventTypes.LoopBack).ShouldBe(1);
    }

    [Fact]
    public async Task Loop_back_limit_then_fallback_then_safe_stop()
    {
        var h = new EngineHarness();
        h.Options.MaxLoopBacks = 1;
        var g = new WorkflowGraph()
            .Add(Node("code", _ => NodeResult.Ok().With("file:a.cs", Guid.NewGuid().ToString())))
            .Add(new NodeDefinition
            {
                Id = "validate", Stage = Stages.Validation, DependsOn = ["code"],
                Handler = new DelegateHandler((_, _) => Task.FromResult(new NodeResult { Success = false, Error = "still broken", RetryTargets = new() { ["code"] = ["x"] } })),
            });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.SafeStopped);
        result.StatusReason.ShouldContain("loop-backs");
        h.Rollback.Reasons.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Planner_expansion_adds_task_nodes_and_join_waits_for_them()
    {
        var h = new EngineHarness();
        var executed = new List<string>();
        var g = new WorkflowGraph()
            .Add(new NodeDefinition
            {
                Id = "plan", Stage = Stages.Planning,
                Handler = new DelegateHandler((_, _) => Task.FromResult(NodeResult.Ok().With("plan", "T1,T2,T3"))),
                Expander = run => run.Require("plan").Split(',').Select(t => new NodeDefinition
                {
                    Id = $"code:{t}", Stage = Stages.Implementation, Group = "impl", DependsOn = ["plan"], ExpandedBy = "plan",
                    Handler = new DelegateHandler((_, _) => { lock (executed) executed.Add(t); return Task.FromResult(NodeResult.Ok().With($"file:{t}", t)); }),
                }),
            })
            .Add(Node("validate", c => c.Run.Artifacts.Keys.Count(k => k.StartsWith("file:")) == 3 ? NodeResult.Ok() : NodeResult.Fail("join ran early"), "plan", "@impl"));
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        executed.Order().ShouldBe(["T1", "T2", "T3"]);
        h.Events.Count(EventTypes.Expanded).ShouldBe(1);
    }

    [Fact]
    public async Task Changing_an_upstream_artifact_replans_only_affected_nodes_on_resume()
    {
        var h = new EngineHarness();
        var runs = new Dictionary<string, int> { ["spec"] = 0, ["design"] = 0, ["docs"] = 0, ["unrelated"] = 0 };
        NodeDefinition Count(string id, string output, string[] inputs, params string[] deps) => new()
        {
            Id = id, Stage = "test", DependsOn = deps, Inputs = inputs,
            Handler = new DelegateHandler((c, _) =>
            {
                runs[id]++;
                var basis = string.Join("+", inputs.Select(i => c.Run.Get(i)));
                return Task.FromResult(NodeResult.Ok().With(output, $"{id}({basis})"));
            }),
        };
        WorkflowGraph Graph() => new WorkflowGraph()
            .Add(Count("spec", "spec", ["requirement"]))
            .Add(Count("unrelated", "license", []))
            .Add(Count("design", "design", ["spec"], "spec"))
            .Add(Count("docs", "docs", ["design"], "design"));

        var (ctx, rec) = h.NewRun();
        ctx.Artifacts["requirement"] = Artifact.Create("requirement", "v1", "human", 1, h.Time.GetUtcNow(), "");
        (await h.RunAsync(Graph(), ctx, rec, ct: Ct)).Status.ShouldBe(RunStatus.Succeeded);

        // Human changes the requirement; resume with persisted state.
        ctx.Artifacts["requirement"] = Artifact.Create("requirement", "v2", "human", 2, h.Time.GetUtcNow(), "");
        var states = await h.Store.GetNodeStatesAsync("r1", Ct);
        var result = await h.RunAsync(Graph(), ctx, rec, states, Ct);

        result.Status.ShouldBe(RunStatus.Succeeded);
        runs.ShouldBe(new Dictionary<string, int> { ["spec"] = 2, ["design"] = 2, ["docs"] = 2, ["unrelated"] = 1 });
        ctx.Get("docs").ShouldBe("docs(design(spec(v2)))");
        h.Events.Count(EventTypes.Replanned).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Safe_stopped_run_resumes_from_the_failed_node()
    {
        var h = new EngineHarness();
        var fixedNow = false;
        var firstRuns = 0;
        WorkflowGraph Graph() => new WorkflowGraph()
            .Add(Node("first", _ => { firstRuns++; return NodeResult.Ok().With("a", "1"); }))
            .Add(new NodeDefinition
            {
                Id = "second", Stage = "test", DependsOn = ["first"], MaxAttempts = 1,
                Handler = new DelegateHandler((_, _) => Task.FromResult(fixedNow ? NodeResult.Ok() : NodeResult.Fail("dependency down"))),
            });
        var (ctx, rec) = h.NewRun();

        (await h.RunAsync(Graph(), ctx, rec, ct: Ct)).Status.ShouldBe(RunStatus.SafeStopped);
        fixedNow = true;
        var resumed = await h.RunAsync(Graph(), ctx, rec, await h.Store.GetNodeStatesAsync("r1", Ct), Ct);

        resumed.Status.ShouldBe(RunStatus.Succeeded);
        firstRuns.ShouldBe(1);
        h.Events.Count(EventTypes.RunResumed).ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_safe_stops_and_persists_pending_state()
    {
        var h = new EngineHarness();
        using var cts = new CancellationTokenSource();
        var g = new WorkflowGraph().Add(new NodeDefinition
        {
            Id = "slow", Stage = "test",
            Handler = new DelegateHandler(async (_, ct) => { await cts.CancelAsync(); await Task.Delay(5000, ct); return NodeResult.Ok(); }),
        });
        var (ctx, rec) = h.NewRun();

        var result = await h.RunAsync(g, ctx, rec, ct: cts.Token);

        result.Status.ShouldBe(RunStatus.SafeStopped);
        result.StatusReason.ShouldContain("interrupt");
        (await h.Store.GetNodeStatesAsync("r1", Ct)).Single().Status.ShouldBe(NodeStatus.Pending);
    }

    [Fact]
    public async Task Audit_chain_records_decisions_and_stays_valid()
    {
        var h = new EngineHarness();
        var g = new WorkflowGraph().Add(Node("design", _ => NodeResult.Ok().With("design", "d").Because("ADR: use 302 redirects")));
        var (ctx, rec) = h.NewRun();

        await h.RunAsync(g, ctx, rec, ct: Ct);

        h.Audit.Entries.ShouldContain(e => e.Action == "decision" && e.Details.Contains("302"));
        (await h.Audit.VerifyAsync(Ct)).IsValid.ShouldBeTrue();
    }
}

