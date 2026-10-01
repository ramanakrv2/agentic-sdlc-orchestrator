using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Orchestrator.Core.Policy;

public enum Autonomy { Auto, Notify, Approve, Forbidden }

/// <summary>Policy-as-code document loaded from policy.yaml.</summary>
public sealed class PolicyDocument
{
    public int Version { get; set; } = 1;
    public AutonomySection Autonomy { get; set; } = new();
    public PathSection Paths { get; set; } = new();
    public DependencySection Dependencies { get; set; } = new();
    public SecretSection Secrets { get; set; } = new();
    public LimitSection Limits { get; set; } = new();
    public QualitySection Quality { get; set; } = new();
    public List<ConventionRule> Conventions { get; set; } = [];

    public sealed class AutonomySection
    {
        public Autonomy Default { get; set; } = Policy.Autonomy.Auto;
        public Dictionary<string, Autonomy> Actions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PathSection
    {
        /// <summary>Agents may never write here (CI config, certificates, production settings).</summary>
        public List<string> Protected { get; set; } = [];
        /// <summary>Platform files owned by the template; agents may not modify them.</summary>
        public List<string> TemplateLocked { get; set; } = [];
    }

    public sealed class DependencySection
    {
        public List<string> Allowlist { get; set; } = [];
    }

    public sealed class SecretSection
    {
        public List<string> Patterns { get; set; } = [];
    }

    public sealed class LimitSection
    {
        public int MaxFileBytes { get; set; } = 60_000;
        public int MaxFilesPerRun { get; set; } = 40;
        public int RetryBudgetPerRun { get; set; } = 10;
        public int MaxSemanticAttempts { get; set; } = 3;
        public int MaxLoopBacks { get; set; } = 2;
    }

    public sealed class QualitySection
    {
        public double MinLineCoverage { get; set; } = 60;
        public bool RequireTestsPass { get; set; } = true;
    }

    public static PolicyDocument FromYaml(string yaml) =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<PolicyDocument>(yaml) ?? new PolicyDocument();
}

/// <summary>Static code convention enforced at the validation gate (scalability/resilience/security rules).</summary>
public sealed class ConventionRule
{
    public string Id { get; set; } = "";
    public string AppliesTo { get; set; } = "**/*.cs";
    public string Pattern { get; set; } = "";
    /// <summary>False (default): pattern must NOT appear. True: pattern MUST appear in every matching file.</summary>
    public bool MustMatch { get; set; }
    public string Message { get; set; } = "";
    public string Category { get; set; } = "quality";
}

public sealed record PolicyViolation(string Rule, string Path, string Message)
{
    public override string ToString() => $"[{Rule}] {Path}: {Message}";
}

public sealed class PolicyEngine(PolicyDocument document)
{
    public PolicyDocument Document { get; } = document;

    public Autonomy Evaluate(string action) =>
        Document.Autonomy.Actions.TryGetValue(action, out var level) ? level : Document.Autonomy.Default;

    /// <summary>Change-control checks for a file an agent wants to write (path relative to the project root).</summary>
    public IReadOnlyList<PolicyViolation> CheckFileWrite(string relativePath, string content)
    {
        var path = Normalize(relativePath);
        var violations = new List<PolicyViolation>();

        if (Path.IsPathRooted(relativePath) || path.Split('/').Contains(".."))
            violations.Add(new("path-traversal", path, "Writes must stay inside the project folder."));

        if (Document.Paths.Protected.Any(g => Glob.IsMatch(g, path)))
            violations.Add(new("protected-path", path, "Path is protected by change-control policy."));

        if (Document.Paths.TemplateLocked.Any(g => Glob.IsMatch(g, path)))
            violations.Add(new("template-locked", path, "Platform file is owned by the template and cannot be modified by agents."));

        if (content.Length > Document.Limits.MaxFileBytes)
            violations.Add(new("file-size", path, $"File exceeds {Document.Limits.MaxFileBytes} bytes."));

        violations.AddRange(ScanSecrets(path, content));
        return violations;
    }

    public IReadOnlyList<PolicyViolation> ScanSecrets(string path, string content) =>
        Document.Secrets.Patterns
            .Where(p => Regex.IsMatch(content, p, RegexOptions.None, TimeSpan.FromSeconds(1)))
            .Select(p => new PolicyViolation("secret-detected", Normalize(path), "Content matches a secret pattern; use configuration/secret store instead."))
            .ToList();

    public IReadOnlyList<PolicyViolation> CheckPackages(string csprojPath, IEnumerable<string> packageIds) =>
        packageIds
            .Where(id => !Document.Dependencies.Allowlist.Any(a => Glob.IsMatch(a, id)))
            .Select(id => new PolicyViolation("dependency-allowlist", Normalize(csprojPath), $"Package '{id}' is not on the approved dependency allowlist."))
            .ToList();

    public IReadOnlyList<PolicyViolation> CheckConventions(string relativePath, string content)
    {
        var path = Normalize(relativePath);
        var violations = new List<PolicyViolation>();
        foreach (var rule in Document.Conventions.Where(r => Glob.IsMatch(r.AppliesTo, path)))
        {
            var found = Regex.IsMatch(content, rule.Pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            if (found != rule.MustMatch)
                violations.Add(new(rule.Id, path, rule.Message));
        }
        return violations;
    }

    private static string Normalize(string p)
    {
        var path = p.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }
}

public static class Glob
{
    /// <summary>Minimal glob: ** = any path segments, * = any chars except '/', ? = one char. Case-insensitive.</summary>
    public static bool IsMatch(string glob, string value)
    {
        var regex = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(value.Replace('\\', '/'), regex, RegexOptions.IgnoreCase);
    }
}
