namespace Orchestrator.Agents;

// Typed contracts exchanged between agents (serialized as JSON artifacts). Exit gates validate them.

public sealed class Spec
{
    public string Title { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> Capabilities { get; set; } = [];
    public List<FunctionalRequirement> FunctionalRequirements { get; set; } = [];
    public NfrHints Nfr { get; set; } = new();
    public List<Ambiguity> Ambiguities { get; set; } = [];
    public List<Clarification> Clarifications { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<string> OutOfScope { get; set; } = [];
}

public sealed class FunctionalRequirement
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Acceptance { get; set; } = "";
}

public sealed class NfrHints
{
    public string? RequestsPerDay { get; set; }
    public string? Availability { get; set; }
    public string? Notes { get; set; }
}

public sealed class Ambiguity
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string? Default { get; set; }
}

public sealed record Clarification(string Question, string Answer);

public sealed class RoutingArtifact
{
    public string Kind { get; set; } = "greenfield";
    public string? ProjectSlug { get; set; }
    public double Confidence { get; set; }
    public string Rationale { get; set; } = "";
    public List<string> Candidates { get; set; } = [];
    public string DecidedBy { get; set; } = "rules";
}

public sealed class RouterVerdict
{
    public string Decision { get; set; } = "unclear";
    public string? Project { get; set; }
    public double Confidence { get; set; }
    public string Rationale { get; set; } = "";
}

public sealed class WorkspaceArtifact
{
    public string Slug { get; set; } = "";
    public string Mode { get; set; } = "greenfield";
    public string BaseVersion { get; set; } = "v0";
    public string Checkpoint { get; set; } = "";
    public List<string> ExistingFeatureFiles { get; set; } = [];
    public bool CardWasStale { get; set; }
}

public sealed class NfrArtifact
{
    public string Profile { get; set; } = "local";
    public string Verdict { get; set; } = "";
    public double RequestsPerDay { get; set; }
    public double PeakRps { get; set; }
    public double AvailabilityTarget { get; set; }
    public bool Overridden { get; set; }
    public List<string> Assumptions { get; set; } = [];
}

public sealed class Impact
{
    public string Summary { get; set; } = "";
    public List<ImpactedFile> Files { get; set; } = [];
    public bool DataModelChange { get; set; }
    public List<string> HotPaths { get; set; } = [];
    public List<string> Risks { get; set; } = [];
}

public sealed class ImpactedFile
{
    public string Path { get; set; } = "";
    public string Change { get; set; } = "";
    public string Risk { get; set; } = "low";
}

public sealed class Design
{
    public string Overview { get; set; } = "";
    public List<EntityDesign> Entities { get; set; } = [];
    public List<EndpointDesign> Endpoints { get; set; } = [];
    public List<NfrPattern> NfrPatterns { get; set; } = [];
    public List<DecisionRecord> Decisions { get; set; } = [];
    public bool SchemaChange { get; set; }
    public List<string> Risks { get; set; } = [];
}

public sealed class EntityDesign
{
    public string Name { get; set; } = "";
    public string Change { get; set; } = "new";
    public List<FieldDesign> Fields { get; set; } = [];
}

public sealed class FieldDesign
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class EndpointDesign
{
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Responses { get; set; } = [];
}

public sealed class NfrPattern
{
    public string Nfr { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string BuildingBlock { get; set; } = "";
    public string Test { get; set; } = "";
}

public sealed class DecisionRecord
{
    public string Title { get; set; } = "";
    public string Decision { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string? Alternatives { get; set; }
}

public sealed class Plan
{
    public List<PlanTask> Tasks { get; set; } = [];
}

public sealed class PlanTask
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "code";
    public string Path { get; set; } = "";
    public string Action { get; set; } = "create";
    public string Description { get; set; } = "";
    public List<string> DependsOn { get; set; } = [];
}

public sealed class Review
{
    public string Verdict { get; set; } = "approve";
    public string Summary { get; set; } = "";
    public List<ReviewFinding> Findings { get; set; } = [];
}

public sealed class ReviewFinding
{
    public string Severity { get; set; } = "low";
    public string File { get; set; } = "";
    public string Issue { get; set; } = "";
    public string? Suggestion { get; set; }
}

public static class ArtifactKeys
{
    public const string Requirement = "requirement";
    public const string Routing = "routing";
    public const string Spec = "spec";
    public const string Nfr = "nfr";
    public const string CapacityReport = "capacity-report";
    public const string Workspace = "workspace";
    public const string Impact = "impact";
    public const string Design = "design";
    public const string Plan = "plan";
    public const string Validation = "validation";
    public const string Review = "review";
    public const string Release = "release";
    public const string FilePrefix = "file:";

    public static string File(string path) => FilePrefix + path.Replace('\\', '/');
}
