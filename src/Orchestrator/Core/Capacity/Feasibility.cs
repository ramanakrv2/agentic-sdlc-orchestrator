using System.Globalization;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orchestrator.Core.Capacity;

/// <summary>Deployment target with an explicit capacity envelope (profiles/*.yaml).</summary>
public sealed class DeploymentProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Rank { get; set; }
    public CapacityEnvelope Capacity { get; set; } = new();
    public Dictionary<string, string> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Strategies { get; set; } = [];
    public List<string> Artifacts { get; set; } = [];
    public string Validation { get; set; } = "";

    public static DeploymentProfile FromYaml(string yaml) =>
        new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build()
            .Deserialize<DeploymentProfile>(yaml);
}

public sealed class CapacityEnvelope
{
    public double MaxPeakRps { get; set; }
    public double MaxWriteRps { get; set; }
    public double MaxStorageGb { get; set; }
    /// <summary>Highest availability target this profile can credibly meet, in percent.</summary>
    public double MaxAvailability { get; set; }
    public bool HorizontalScaling { get; set; }
    public string Basis { get; set; } = "";
}

/// <summary>Non-functional requirements, captured from the requirement text, human answers or defaults (assumptions).</summary>
public sealed record NfrInput
{
    public double RequestsPerDay { get; init; } = 100_000;
    public double PeakFactor { get; init; } = 10;
    /// <summary>Reads per write (redirects per link created).</summary>
    public double ReadWriteRatio { get; init; } = 100;
    public double AvailabilityTarget { get; init; } = 99.0;
    public int RetentionDays { get; init; } = 90;
    public int WriteRecordBytes { get; init; } = 500;
    public int ReadEventBytes { get; init; } = 200;
    public List<string> Assumptions { get; init; } = [];
}

public enum Verdict { Feasible, FeasibleWithRisks, NotFeasible }

public sealed record CapacityCheck(string Dimension, double Required, double Limit, string Unit, Verdict Verdict, string Note);

public sealed record FeasibilityReport(
    DeploymentProfile Profile,
    NfrInput Nfr,
    double AvgRps,
    double PeakRps,
    double PeakWriteRps,
    double StorageGb,
    IReadOnlyList<CapacityCheck> Checks,
    Verdict Verdict,
    DeploymentProfile? Recommended)
{
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine($"# Capacity & Feasibility Report — profile `{Profile.Id}`");
        sb.AppendLine();
        sb.AppendLine($"**Verdict: {Icon(Verdict)} {Verdict}**");
        sb.AppendLine();
        sb.AppendLine("## Derived load");
        sb.AppendLine($"- Requests/day: {Nfr.RequestsPerDay.ToString("N0", ci)} → average **{AvgRps.ToString("N1", ci)} RPS**, peak (×{Nfr.PeakFactor}) **{PeakRps.ToString("N0", ci)} RPS**");
        sb.AppendLine($"- Read:write ratio {Nfr.ReadWriteRatio}:1 → peak writes **{PeakWriteRps.ToString("N1", ci)} /s**");
        sb.AppendLine($"- Storage over {Nfr.RetentionDays} days retention: **{StorageGb.ToString("N1", ci)} GB**");
        sb.AppendLine($"- Availability target: **{Nfr.AvailabilityTarget}%**");
        sb.AppendLine();
        sb.AppendLine("## Checks against the profile envelope");
        sb.AppendLine("| Dimension | Required | Profile limit | Verdict | Note |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in Checks)
            sb.AppendLine($"| {c.Dimension} | {c.Required.ToString("N1", ci)} {c.Unit} | {c.Limit.ToString("N1", ci)} {c.Unit} | {Icon(c.Verdict)} | {c.Note} |");
        sb.AppendLine();
        sb.AppendLine($"_Envelope basis: {Profile.Capacity.Basis}_");
        if (Nfr.Assumptions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Assumptions");
            foreach (var a in Nfr.Assumptions) sb.AppendLine($"- {a}");
        }
        if (Recommended is not null && Recommended.Id != Profile.Id)
        {
            sb.AppendLine();
            sb.AppendLine($"## Recommendation\nSmallest profile that satisfies all checks: **{Recommended.Name}** (`{Recommended.Id}`).");
        }
        sb.AppendLine();
        sb.AppendLine("## Strategies for this profile");
        foreach (var s in Profile.Strategies) sb.AppendLine($"- {s}");
        return sb.ToString();
    }

    private static string Icon(Verdict v) => v switch { Verdict.Feasible => "✅", Verdict.FeasibleWithRisks => "⚠️", _ => "❌" };
}

/// <summary>
/// Deterministic capacity arithmetic — deliberately not an LLM, because a wrong "infeasible" is as bad as a wrong "feasible".
/// </summary>
public static class FeasibilityAnalyzer
{
    /// <summary>Utilisation above this fraction of the envelope is flagged as a risk (no headroom).</summary>
    public const double HeadroomThreshold = 0.7;

    public static FeasibilityReport Analyze(NfrInput nfr, DeploymentProfile profile, IReadOnlyList<DeploymentProfile> allProfiles)
    {
        var report = Evaluate(nfr, profile);
        DeploymentProfile? recommended = null;
        if (report.Verdict != Verdict.Feasible)
            recommended = allProfiles.OrderBy(p => p.Rank).FirstOrDefault(p => Evaluate(nfr, p).Verdict == Verdict.Feasible);
        return report with { Recommended = recommended };
    }

    private static FeasibilityReport Evaluate(NfrInput nfr, DeploymentProfile profile)
    {
        var avg = nfr.RequestsPerDay / 86_400d;
        var peak = avg * nfr.PeakFactor;
        var peakWrites = peak / (nfr.ReadWriteRatio + 1);
        var writesPerDay = nfr.RequestsPerDay / (nfr.ReadWriteRatio + 1);
        var readsPerDay = nfr.RequestsPerDay - writesPerDay;
        var storageGb = (writesPerDay * nfr.WriteRecordBytes + readsPerDay * nfr.ReadEventBytes) * nfr.RetentionDays / 1e9;
        var cap = profile.Capacity;

        var checks = new List<CapacityCheck>
        {
            Ratio("Peak throughput", peak, cap.MaxPeakRps, "RPS", cap.HorizontalScaling ? "Scale out horizontally (HPA/replicas)." : "Single host; cannot scale out."),
            Ratio("Peak writes", peakWrites, cap.MaxWriteRps, "writes/s", "Write path is the usual bottleneck (single-writer DB on local)."),
            Ratio("Storage", storageGb, cap.MaxStorageGb, "GB", "Retention × daily growth; consider TTL/archival of click events."),
            nfr.AvailabilityTarget <= cap.MaxAvailability
                ? new CapacityCheck("Availability", nfr.AvailabilityTarget, cap.MaxAvailability, "%", Verdict.Feasible, "Within what the profile can deliver.")
                : new CapacityCheck("Availability", nfr.AvailabilityTarget, cap.MaxAvailability, "%", Verdict.NotFeasible,
                    "Target needs redundancy (multiple nodes/zones, replicated DB) this profile does not have."),
        };

        var verdict = checks.Any(c => c.Verdict == Verdict.NotFeasible) ? Verdict.NotFeasible
            : checks.Any(c => c.Verdict == Verdict.FeasibleWithRisks) ? Verdict.FeasibleWithRisks
            : Verdict.Feasible;

        return new FeasibilityReport(profile, nfr, avg, peak, peakWrites, storageGb, checks, verdict, null);
    }

    private static CapacityCheck Ratio(string dim, double required, double limit, string unit, string note)
    {
        if (limit <= 0) return new(dim, required, limit, unit, Verdict.FeasibleWithRisks, "Limit not defined for this profile.");
        var utilisation = required / limit;
        var verdict = utilisation <= HeadroomThreshold ? Verdict.Feasible : utilisation <= 1.0 ? Verdict.FeasibleWithRisks : Verdict.NotFeasible;
        var pct = (utilisation * 100).ToString("N0", CultureInfo.InvariantCulture);
        return new(dim, required, limit, unit, verdict, $"{pct}% of envelope. {note}");
    }

    /// <summary>Parses "1M/day", "500k per day", "1,000,000", "50 rps" into requests/day.</summary>
    public static double? ParseLoad(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(",", "").Replace("_", "");
        var perSecond = t.Contains("rps") || t.Contains("/s") || t.Contains("per second");
        var numberPart = new string(t.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        var rest = t[numberPart.Length..].TrimStart();
        if (rest.StartsWith("billion") || rest.StartsWith('b')) n *= 1e9;
        else if (rest.StartsWith("million") || rest.StartsWith('m')) n *= 1e6;
        else if (rest.StartsWith("thousand") || rest.StartsWith('k')) n *= 1e3;
        return perSecond ? n * 86_400 : n;
    }
}
