using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Agents;
using Orchestrator.Agents.Nodes;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Llm;
using Orchestrator.Core.Policy;
using Orchestrator.Core.Projects;
using Orchestrator.Infrastructure;
using Orchestrator.Tests.Governance;

namespace Orchestrator.Tests.Agents;

public sealed class AgentTests
{
    private static AgentServices Services()
    {
        var home = Path.GetDirectoryName(PolicyEngineTests.RepoFile("policy.yaml"))!;
        return new AgentServices(Substitute.For<ILlmClient>(), Substitute.For<IHumanInteraction>(), Substitute.For<IProjectRegistry>(),
            new LexicalProjectMatcher(), new YamlProfileCatalog(Path.Combine(home, "profiles")),
            new PolicyEngine(PolicyDocument.FromYaml(File.ReadAllText(Path.Combine(home, "policy.yaml")))),
            Substitute.For<IProcessRunner>(), new AgentPaths(home, Path.Combine(home, "templates", "webapi")), TimeProvider.System, NullLoggerFactory.Instance);
    }

    [Fact]
    public void Sdlc_graph_is_a_valid_dag_with_governed_checkpoints()
    {
        var graph = SdlcPipeline.Build(Services());
        var order = graph.Validate();

        order.First().ShouldBe("route");
        order.Last().ShouldBe("release");
        graph["nfr"].Action.ShouldBe("spec.approve");
        graph["release"].Action.ShouldBe("release.publish");
        graph["validate"].DependsOn.ShouldContain("@impl");
        graph.Nodes.Where(n => n.Group == "impl").Select(n => n.Id).ShouldBe(["docs", "deploy"], ignoreOrder: true);
    }

    private static NodeContext PlanContext(string mode = "greenfield", params string[] existing)
    {
        var run = new RunContext { RunId = "r", Requirement = "x" };
        run.Artifacts[ArtifactKeys.Workspace] = Artifact.Create(ArtifactKeys.Workspace,
            JsonText.Serialize(new WorkspaceArtifact { Slug = "s", Mode = mode, ExistingFeatureFiles = [.. existing] }), "workspace", 1, DateTimeOffset.UtcNow, "");
        return new NodeContext { Run = run, Node = new NodeDefinition { Id = "plan", Stage = "planning", Handler = null! }, Attempt = 1, IsFallback = false };
    }

    private static NodeResult PlanResult(params PlanTask[] tasks) => NodeResult.Ok().With(ArtifactKeys.Plan, JsonText.Serialize(new Plan { Tasks = [.. tasks] }));

    [Fact]
    public async Task Plan_gate_accepts_a_well_formed_plan()
    {
        var gate = PlanNode.Gate(Services());
        var result = await gate.CheckAsync(PlanContext(), PlanResult(
            new PlanTask { Id = "T1", Kind = "code", Path = "src/App/Features/Urls/ShortUrl.cs" },
            new PlanTask { Id = "T2", Kind = "code", Path = "src/App/Features/FeatureRegistration.cs", Action = "modify", DependsOn = ["T1"] },
            new PlanTask { Id = "T3", Kind = "test", Path = "tests/App.Tests/Features/Urls/UrlTests.cs", DependsOn = ["T1"] }), TestContext.Current.CancellationToken);

        result.Violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_gate_rejects_locked_paths_cycles_missing_tests_and_unknown_modifies()
    {
        var gate = PlanNode.Gate(Services());
        var result = await gate.CheckAsync(PlanContext(), PlanResult(
            new PlanTask { Id = "T1", Kind = "code", Path = "src/App/Platform/Caching/CacheService.cs", DependsOn = ["T2"] },
            new PlanTask { Id = "T2", Kind = "code", Path = "src/App/Features/Urls/Missing.cs", Action = "modify", DependsOn = ["T1"] }), TestContext.Current.CancellationToken);

        result.Passed.ShouldBeFalse();
        result.Violations.ShouldContain(v => v.Contains("test task"));
        result.Violations.ShouldContain(v => v.Contains("FeatureRegistration"));
        result.Violations.ShouldContain(v => v.Contains("does not exist"));
        result.Violations.ShouldContain(v => v.Contains("cycle"));
        result.Violations.ShouldContain(v => v.Contains("must be under") || v.Contains("template-locked"));
    }

    [Fact]
    public async Task Code_gate_feeds_back_convention_violations()
    {
        var task = new PlanTask { Id = "T1", Kind = "code", Path = "src/App/Features/Urls/UrlService.cs" };
        var gate = CoderNode.Gate(Services(), task);
        var code = "namespace App.Features.Urls;\npublic class S\n{\n    private static int _hits;\n    public string Now() => DateTime.UtcNow.ToString();\n}";

        var result = await gate.CheckAsync(PlanContext(), NodeResult.Ok().With(ArtifactKeys.File(task.Path), code), TestContext.Current.CancellationToken);

        result.Violations.ShouldContain(v => v.Contains("stateless-instances"));
        result.Violations.ShouldContain(v => v.Contains("use-timeprovider"));
    }

    [Fact]
    public void Brownfield_docs_merge_updates_existing_endpoint_rows_and_appends_new_ones()
    {
        var existing = "| Method | Route | Description |\n|---|---|---|\n| POST | `/api/v1/urls` | Create |\n| GET | `/{code}` | Redirect |\n\n## Run locally";

        var merged = DocsNode.UpsertEndpointRows(existing,
            [new EndpointDesign { Method = "GET", Route = "/{code}", Description = "Redirect; 410 when expired" },
             new EndpointDesign { Method = "DELETE", Route = "/api/v1/urls/{code}", Description = "Delete" }], withResponses: false);

        merged.ShouldContain("| POST | `/api/v1/urls` | Create |");                  // untouched v1 row kept
        merged.ShouldContain("| GET | `/{code}` | Redirect; 410 when expired |");     // changed row replaced
        merged.IndexOf("DELETE").ShouldBeLessThan(merged.IndexOf("## Run locally")); // new row appended inside the table
    }

    [Fact]
    public void Extracts_code_from_fenced_llm_output()
    {
        AgentServices.ExtractCode("Here you go:\n```csharp\nnamespace A;\nclass B {}\n```\nHope it helps").ShouldBe("namespace A;\nclass B {}\n");
        AgentServices.ExtractCode("namespace A;").ShouldBe("namespace A;\n");
    }

    [Fact]
    public void Parses_json_even_when_wrapped_in_prose() =>
        JsonText.ParseLlmJson<RouterVerdict>("Sure! {\"decision\":\"existing\",\"project\":\"url-shortener\",\"confidence\":0.9} done")
            .Project.ShouldBe("url-shortener");

    [Fact]
    public void Deployment_gate_rejects_mutable_tags_and_missing_probes()
    {
        var result = DeployNode.Gate.CheckAsync(PlanContext(), NodeResult.Ok().With("file:deploy/k8s/deployment.yaml", "image: app:latest\nresources:\n"), default).Result;
        result.Violations.ShouldContain(v => v.Contains(":latest"));
        result.Violations.ShouldContain(v => v.Contains("livenessProbe"));
    }
}
