using Orchestrator.Core.Capacity;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Metrics;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Policy;
using Orchestrator.Core.Projects;

namespace Orchestrator.Tests.Governance;

public sealed class PolicyEngineTests
{
    private static readonly PolicyEngine Policy = new(PolicyDocument.FromYaml(File.ReadAllText(RepoFile("policy.yaml"))));

    internal static string RepoFile(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, relative))) return Path.Combine(d.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    [Theory]
    [InlineData("release.publish", Autonomy.Approve)]
    [InlineData("schema.change", Autonomy.Approve)]
    [InlineData("project.create", Autonomy.Notify)]
    [InlineData("secrets.write", Autonomy.Forbidden)]
    [InlineData("anything.else", Autonomy.Auto)]
    public void Autonomy_levels_come_from_policy(string action, Autonomy expected) => Policy.Evaluate(action).ShouldBe(expected);

    [Theory]
    [InlineData("src/App/Platform/Caching/CacheService.cs", "template-locked")]
    [InlineData("src/App/Program.cs", "template-locked")]
    [InlineData(".github/workflows/ci.yml", "protected-path")]
    [InlineData("../outside.cs", "path-traversal")]
    public void Change_control_blocks_writes_outside_feature_code(string path, string rule) =>
        Policy.CheckFileWrite(path, "class X {}").ShouldContain(v => v.Rule == rule);

    [Fact]
    public void Feature_files_are_writable() =>
        Policy.CheckFileWrite("src/App/Features/Urls/UrlService.cs", "namespace App.Features.Urls;").ShouldBeEmpty();

    [Theory]
    [InlineData("var conn = \"Server=x;Password=SuperSecret123\";")]
    [InlineData("var key = \"AKIAABCDEFGHIJKLMNOP\";")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void Secrets_are_detected(string content) =>
        Policy.CheckFileWrite("src/App/Features/X.cs", content).ShouldContain(v => v.Rule == "secret-detected");

    [Theory]
    [InlineData("src/App/Features/A/S.cs", "var x = service.GetAsync().Result;", "no-sync-over-async")]
    [InlineData("src/App/Features/A/S.cs", "    private static int _counter = 0;", "stateless-instances")]
    [InlineData("src/App/Features/A/S.cs", "var now = DateTime.UtcNow;", "use-timeprovider")]
    [InlineData("src/App/Features/A/AEndpoints.cs", "app.MapGet(\"/\", (AppDbContext db) => db);", "endpoints-no-dbcontext")]
    [InlineData("src/App/Features/A/R.cs", "db.Database.ExecuteSqlRaw($\"DELETE FROM t WHERE id={id}\");", "no-raw-sql-concatenation")]
    public void Conventions_flag_unscalable_or_unsafe_code(string path, string code, string rule) =>
        Policy.CheckConventions(path, code).ShouldContain(v => v.Rule == rule);

    [Theory]
    [InlineData("    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);")]
    [InlineData("    public static string Key(string code) => $\"url:{code}\";")]
    [InlineData("    private static partial Regex Pattern();")]
    [InlineData("    public const int Length = 7;")]
    public void Conventions_allow_immutable_statics_and_methods(string code) =>
        Policy.CheckConventions("src/App/Features/A/S.cs", code).ShouldBeEmpty();

    [Fact]
    public void Dependency_allowlist_rejects_unknown_packages()
    {
        Policy.CheckPackages("src/App/App.csproj", ["Microsoft.EntityFrameworkCore.Sqlite", "Polly.Core"]).ShouldBeEmpty();
        Policy.CheckPackages("src/App/App.csproj", ["LeftPad.Unvetted"]).ShouldHaveSingleItem().Rule.ShouldBe("dependency-allowlist");
    }

    [Theory]
    [InlineData("src/**/*.cs", "src/App/Features/A/B.cs", true)]
    [InlineData("src/App/*.csproj", "src/App/App.csproj", true)]
    [InlineData("src/App/*.csproj", "src/App/Sub/App.csproj", false)]
    [InlineData("**/.github/**", ".github/workflows/x.yml", true)]
    public void Glob_matching(string glob, string path, bool expected) => Glob.IsMatch(glob, path).ShouldBe(expected);
}

public sealed class AuditChainTests
{
    private static List<AuditEntry> Chain(int n)
    {
        var entries = new List<AuditEntry>();
        var prev = AuditChain.Genesis;
        for (var i = 1; i <= n; i++)
        {
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + i);
            var hash = AuditChain.ComputeHash(i, ts, "r", "actor", "action", "t", $"d{i}", prev);
            entries.Add(new AuditEntry(i, ts, "r", "actor", "action", "t", $"d{i}", prev, hash));
            prev = hash;
        }
        return entries;
    }

    [Fact]
    public void Intact_chain_verifies() => AuditChain.Verify(Chain(5)).IsValid.ShouldBeTrue();

    [Fact]
    public void Modified_entry_is_detected()
    {
        var chain = Chain(5);
        chain[2] = chain[2] with { Details = "approved by someone else" };
        var v = AuditChain.Verify(chain);
        v.IsValid.ShouldBeFalse();
        v.BrokenAtSequence.ShouldBe(3);
    }

    [Fact]
    public void Removed_entry_is_detected()
    {
        var chain = Chain(5);
        chain.RemoveAt(1);
        AuditChain.Verify(chain).BrokenAtSequence.ShouldBe(3);
    }
}

public sealed class MetricsCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RunEvent E(string run, string type, int seconds, string? node = null, string? stage = null, double? ms = null, Dictionary<string, string>? data = null) =>
        new(run, type, T0.AddSeconds(seconds), node, stage, ms, data);

    [Fact]
    public void Computes_success_rate_mttr_retries_and_latency_excluding_human_wait()
    {
        var events = new[]
        {
            E("r1", EventTypes.RunStarted, 0),
            E("r1", EventTypes.NodeAttemptFailed, 10, "code:T1", "implementation"),
            E("r1", EventTypes.NodeRetried, 11, "code:T1", "implementation"),
            E("r1", EventTypes.NodeSucceeded, 40, "code:T1", "implementation", 30_000),
            E("r1", EventTypes.ApprovalResolved, 50, "release", "release", 20_000),
            E("r1", EventTypes.RunCompleted, 100, data: new() { ["status"] = "Succeeded" }),
            E("r2", EventTypes.RunStarted, 0),
            E("r2", EventTypes.NodeFailed, 5, "validate", "validation"),
            E("r2", EventTypes.RolledBack, 6),
            E("r2", EventTypes.RunCompleted, 10, data: new() { ["status"] = "SafeStopped" }),
            E("r2", EventTypes.LlmCall, 3, data: new() { ["source"] = "Replay", ["tokens"] = "0" }),
            E("r1", EventTypes.LlmCall, 3, ms: 2000, data: new() { ["source"] = "Live", ["tokens"] = "1500" }),
        };

        var m = MetricsCalculator.Compute(events);

        m.Runs.ShouldBe(2);
        m.RunSuccessRate.ShouldBe(0.5);
        m.SemanticRetries.ShouldBe(1);
        m.Rollbacks.ShouldBe(1);
        m.RollbackFrequency.ShouldBe(0.5);
        m.MttrMs.ShouldBe(30_000);                // failed at 10s, recovered at 40s
        m.AvgEndToEndMs.ShouldBe((100_000 + 10_000) / 2.0);
        m.AvgAgentTimeMs.ShouldBe((80_000 + 10_000) / 2.0); // r1 minus 20s human wait
        m.LlmLiveCalls.ShouldBe(1);
        m.LlmReplayCalls.ShouldBe(1);
        m.LlmTokens.ShouldBe(1500);
        m.Stages.Single(s => s.Stage == "implementation").AttemptFailures.ShouldBe(1);
    }
}

public sealed class FeasibilityTests
{
    private static readonly IReadOnlyList<DeploymentProfile> Profiles = Directory
        .EnumerateFiles(Path.GetDirectoryName(PolicyEngineTests.RepoFile("profiles/local.yaml"))!, "*.yaml")
        .Select(f => DeploymentProfile.FromYaml(File.ReadAllText(f))).OrderBy(p => p.Rank).ToList();

    private static DeploymentProfile P(string id) => Profiles.Single(p => p.Id == id);

    [Fact]
    public void One_million_per_day_is_feasible_locally()
    {
        // 1M/day ≈ 11.6 RPS average, ≈116 RPS at a 10× peak — a laptop can serve that.
        var r = FeasibilityAnalyzer.Analyze(new NfrInput { RequestsPerDay = 1_000_000 }, P("local"), Profiles);
        r.AvgRps.ShouldBe(11.57, 0.01);
        r.PeakRps.ShouldBe(115.7, 0.1);
        r.Verdict.ShouldNotBe(Verdict.NotFeasible);
    }

    [Fact]
    public void One_hundred_million_per_day_is_not_feasible_locally_and_kubernetes_is_recommended()
    {
        var r = FeasibilityAnalyzer.Analyze(new NfrInput { RequestsPerDay = 100_000_000 }, P("local"), Profiles);
        r.Verdict.ShouldBe(Verdict.NotFeasible);
        r.Checks.ShouldContain(c => c.Dimension == "Peak throughput" && c.Verdict == Verdict.NotFeasible);
        r.Recommended!.Id.ShouldBe("kubernetes");
    }

    [Fact]
    public void High_availability_target_is_not_feasible_on_a_single_host()
    {
        var r = FeasibilityAnalyzer.Analyze(new NfrInput { RequestsPerDay = 10_000, AvailabilityTarget = 99.9 }, P("local"), Profiles);
        r.Verdict.ShouldBe(Verdict.NotFeasible);
        r.Checks.Single(c => c.Dimension == "Availability").Verdict.ShouldBe(Verdict.NotFeasible);
        r.ToMarkdown().ShouldContain("Recommendation");
    }

    [Theory]
    [InlineData("1M/day", 1_000_000)]
    [InlineData("1 million", 1_000_000)]
    [InlineData("500k", 500_000)]
    [InlineData("1,000,000", 1_000_000)]
    [InlineData("50 rps", 50 * 86_400)]
    public void Parses_load_expressions(string text, double expected) => FeasibilityAnalyzer.ParseLoad(text).ShouldBe(expected);
}

public sealed class RoutingTests
{
    private static readonly ProjectCard UrlShortener = new()
    {
        Slug = "url-shortener", Name = "URL Shortener", Summary = "Creates short links and redirects with click analytics.",
        Aliases = ["link shortener", "short links"], Capabilities = ["create short url", "custom alias", "redirect", "click analytics"],
        Entities = ["ShortUrl", "ClickEvent"], Endpoints = ["POST /api/v1/urls", "GET /{code}"],
    };

    private static readonly ProjectCard Todo = new()
    {
        Slug = "todo-api", Name = "Todo API", Summary = "Task lists with due dates.", Capabilities = ["create task", "complete task"], Entities = ["TodoItem"],
    };

    [Theory]
    [InlineData("Add expiry dates to short links", ChangeIntent.Change)]
    [InlineData("Fix the redirect bug", ChangeIntent.Change)]
    [InlineData("Build a new inventory service", ChangeIntent.New)]
    public void Classifies_change_intent(string text, ChangeIntent expected) => IntentClassifier.Classify(text).ShouldBe(expected);

    [Fact]
    public void Lexical_matcher_ranks_the_right_project_first()
    {
        var candidates = new LexicalProjectMatcher().FindCandidates("Add link expiration to the URL shortener redirect", [Todo, UrlShortener]);
        candidates.First().Card.Slug.ShouldBe("url-shortener");
        candidates.First().MatchedTerms.ShouldContain("redirect");
    }

    [Fact]
    public void Strong_match_routes_to_brownfield()
    {
        var c = new LexicalProjectMatcher().FindCandidates("Add expiry to short links in the URL shortener", [UrlShortener, Todo]);
        var d = RoutingRules.Decide(ChangeIntent.Change, c, llmConfidence: 0.9, llmChoice: "url-shortener");
        d.Kind.ShouldBe(RoutingKind.Brownfield);
        d.ProjectSlug.ShouldBe("url-shortener");
    }

    [Fact]
    public void No_match_with_new_intent_is_greenfield_but_change_intent_asks_the_human()
    {
        RoutingRules.Decide(ChangeIntent.New, []).Kind.ShouldBe(RoutingKind.Greenfield);
        RoutingRules.Decide(ChangeIntent.Change, []).Kind.ShouldBe(RoutingKind.AskHuman);
    }

    [Fact]
    public void Partial_match_asks_the_human() =>
        RoutingRules.Decide(ChangeIntent.Change, [new ProjectCandidate(UrlShortener, 0.6, ["link"])]).Kind.ShouldBe(RoutingKind.AskHuman);
}
