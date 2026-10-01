using Orchestrator.Agents.Nodes;
using Orchestrator.Core.Engine;

namespace Orchestrator.Agents;

/// <summary>
/// The SDLC dependency graph. Static skeleton + dynamic expansion by the planner:
/// <code>
/// route → analyze → nfr[approve spec] → workspace → impact? → design → plan ─┬─ code:T* / test:T* (task DAG) ─┐
///                                                                           ├─ docs                         ├─► validate ⟲ → review → release[approve]
///                                                                           └─ deploy                       ┘
/// </code>
/// Every node declares the artifacts it reads (re-planning), its gates, attempts and fallback.
/// </summary>
public static class SdlcPipeline
{
    public const string ImplGroup = "impl";

    public static WorkflowGraph Build(AgentServices s)
    {
        var attempts = s.Policy.Document.Limits.MaxSemanticAttempts;
        var g = new WorkflowGraph();

        g.Add(new NodeDefinition
        {
            Id = "route", Stage = Stages.Intake, Handler = new RouteNode(s), Fallback = new RouteNode(s), MaxAttempts = 2,
            Inputs = [ArtifactKeys.Requirement],
            EntryGates = [Gate.RequiresArtifacts(ArtifactKeys.Requirement)],
            ExitGates = [Gate.Produces(ArtifactKeys.Routing)],
        });
        g.Add(new NodeDefinition
        {
            Id = "analyze", Stage = Stages.Requirements, Handler = new AnalyzeNode(s), Fallback = new AnalyzeNode(s), MaxAttempts = attempts,
            DependsOn = ["route"], Inputs = [ArtifactKeys.Requirement, ArtifactKeys.Routing],
            ExitGates = [Gate.Produces(ArtifactKeys.Spec), AnalyzeNode.Gate],
        });
        g.Add(new NodeDefinition
        {
            Id = "nfr", Stage = Stages.Requirements, Handler = new NfrNode(s), Action = "spec.approve",
            DependsOn = ["analyze"], Inputs = [ArtifactKeys.Spec, ArtifactKeys.Routing],
            ExitGates = [Gate.Produces(ArtifactKeys.Nfr, ArtifactKeys.CapacityReport)],
        });
        g.Add(new NodeDefinition
        {
            Id = "workspace", Stage = Stages.Workspace, Handler = new WorkspaceNode(s), MaxAttempts = 1,
            DependsOn = ["nfr"], Inputs = [ArtifactKeys.Routing],
            ExitGates = [Gate.Produces(ArtifactKeys.Workspace)],
        });
        g.Add(new NodeDefinition
        {
            Id = "impact", Stage = Stages.Architecture, Handler = new ImpactNode(s), Fallback = new ImpactNode(s), MaxAttempts = attempts,
            DependsOn = ["workspace"], Inputs = [ArtifactKeys.Spec, ArtifactKeys.Workspace],
            Condition = run => run.Get(ArtifactKeys.Workspace)?.Contains("\"brownfield\"") == true,
            ExitGates = [Gate.Produces(ArtifactKeys.Impact)],
        });
        g.Add(new NodeDefinition
        {
            Id = "design", Stage = Stages.Architecture, Handler = new DesignNode(s), Fallback = new DesignNode(s), MaxAttempts = attempts,
            Action = "design.review",
            DependsOn = ["impact"], Inputs = [ArtifactKeys.Spec, ArtifactKeys.Nfr, ArtifactKeys.Impact],
            ExitGates = [Gate.Produces(ArtifactKeys.Design), DesignNode.Gate],
        });
        g.Add(new NodeDefinition
        {
            Id = "plan", Stage = Stages.Planning, Handler = new PlanNode(s), Fallback = new PlanNode(s), MaxAttempts = attempts,
            DependsOn = ["design"], Inputs = [ArtifactKeys.Design, ArtifactKeys.Spec],
            ExitGates = [Gate.Produces(ArtifactKeys.Plan), PlanNode.Gate(s)],
            Expander = run => ExpandPlan(s, run, attempts),
        });
        g.Add(new NodeDefinition
        {
            Id = "docs", Stage = Stages.Documentation, Group = ImplGroup, Handler = new DocsNode(s), Fallback = new DocsNode(s, deterministicReadme: true),
            MaxAttempts = 2, DependsOn = ["plan"], Inputs = [ArtifactKeys.Spec, ArtifactKeys.Design, ArtifactKeys.Nfr],
            ExitGates = [DocsNode.Gate], OnCommit = DocsNode.Commit(s),
        });
        g.Add(new NodeDefinition
        {
            Id = "deploy", Stage = Stages.Deployment, Group = ImplGroup, Handler = new DeployNode(s), MaxAttempts = 1,
            DependsOn = ["plan"], Inputs = [ArtifactKeys.Nfr],
            ExitGates = [DeployNode.Gate], OnCommit = DocsNode.Commit(s),
        });
        g.Add(new NodeDefinition
        {
            Id = "validate", Stage = Stages.Validation, Handler = new ValidateNode(s), MaxAttempts = 2,
            DependsOn = ["plan", "@" + ImplGroup], Inputs = [ArtifactKeys.FilePrefix + "*"],
        });
        g.Add(new NodeDefinition
        {
            Id = "review", Stage = Stages.Review, Handler = new ReviewNode(s), Fallback = new ReviewNode(s), MaxAttempts = 2,
            DependsOn = ["validate"], Inputs = [ArtifactKeys.Validation, ArtifactKeys.FilePrefix + "*"],
            ExitGates = [Gate.Produces(ArtifactKeys.Review)],
        });
        g.Add(new NodeDefinition
        {
            Id = "release", Stage = Stages.Release, Handler = new ReleaseNode(s), Action = "release.publish", MaxAttempts = 1,
            DependsOn = ["review"], Inputs = [ArtifactKeys.Review, ArtifactKeys.Validation, ArtifactKeys.FilePrefix + "*"],
            ExitGates = [Gate.Produces(ArtifactKeys.Release)], OnCommit = ReleaseNode.Commit(s),
        });

        g.Validate();
        return g;
    }

    /// <summary>Planner output → one node per file task; task dependencies become graph edges (sequential + parallel paths).</summary>
    private static IEnumerable<NodeDefinition> ExpandPlan(AgentServices s, RunContext run, int attempts)
    {
        var plan = AgentServices.Read<Plan>(run, ArtifactKeys.Plan);
        var byId = plan.Tasks.ToDictionary(t => t.Id);
        foreach (var task in plan.Tasks)
        {
            var deps = task.DependsOn.Where(byId.ContainsKey).Select(d => byId[d]).ToList();
            yield return new NodeDefinition
            {
                Id = PlanNode.NodeId(task),
                Stage = task.Kind == "test" ? Stages.Testing : Stages.Implementation,
                Group = ImplGroup,
                ExpandedBy = "plan",
                Handler = new CoderNode(s, task),
                Fallback = new CoderNode(s, task),
                MaxAttempts = attempts,
                DependsOn = ["plan", .. deps.Select(PlanNode.NodeId)],
                Inputs = [ArtifactKeys.Design, ArtifactKeys.Plan, .. deps.Select(d => ArtifactKeys.File(d.Path))],
                ExitGates = [CoderNode.Gate(s, task)],
                OnCommit = CoderNode.Commit(s, task),
            };
        }
    }
}
