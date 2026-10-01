using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Llm;
using Orchestrator.Core.Policy;
using Orchestrator.Core.Projects;
using Orchestrator.Infrastructure;

namespace Orchestrator.Agents;

public sealed record AgentPaths(string Home, string TemplateDir)
{
    public string PlatformDocPath => Path.Combine(TemplateDir, "PLATFORM.md");
    public string PatternCatalogPath => Path.Combine(Home, "docs", "standards", "nfr-patterns.md");
}

/// <summary>Everything an agent may use. Agents never talk to each other directly — only via artifacts.</summary>
public sealed class AgentServices(
    ILlmClient llm,
    IHumanInteraction human,
    IProjectRegistry projects,
    IProjectMatcher matcher,
    IProfileCatalog profiles,
    PolicyEngine policy,
    IProcessRunner processes,
    AgentPaths paths,
    TimeProvider time,
    ILoggerFactory loggers)
{
    private string? _platformDoc;
    private string? _patternCatalog;

    public ILlmClient Llm => llm;
    public IHumanInteraction Human => human;
    public IProjectRegistry Projects => projects;
    public IProjectMatcher Matcher => matcher;
    public IProfileCatalog Profiles => profiles;
    public PolicyEngine Policy => policy;
    public IProcessRunner Processes => processes;
    public AgentPaths Paths => paths;
    public TimeProvider Time => time;
    public ILogger Logger(string name) => loggers.CreateLogger(name);

    public string PlatformDoc => _platformDoc ??= File.Exists(paths.PlatformDocPath) ? File.ReadAllText(paths.PlatformDocPath).Replace("\r\n", "\n") : "";
    public string PatternCatalog => _patternCatalog ??= File.Exists(paths.PatternCatalogPath) ? File.ReadAllText(paths.PatternCatalogPath).Replace("\r\n", "\n") : "";

    public string ProjectDir(RunContext run) =>
        Path.Combine(projects.WorkspaceRoot, run.ProjectSlug ?? throw new InvalidOperationException("Project not selected yet."));

    /// <summary>
    /// Calls the LLM and parses JSON. Parse errors surface as node failures, which the engine retries with the
    /// error as feedback. In fallback mode the curated replay is requested with the original (feedback-free) prompt.
    /// </summary>
    public async Task<T> AskJsonAsync<T>(NodeContext ctx, string agent, string role, string instructions, string context, CancellationToken ct, int maxTokens = 3000)
    {
        var response = await CallAsync(ctx, agent, Prompts.System(role) + " " + Prompts.JsonOnly, instructions, context, json: true, maxTokens, ct);
        return JsonText.ParseLlmJson<T>(response.Content);
    }

    public async Task<string> AskTextAsync(NodeContext ctx, string agent, string role, string instructions, string context, CancellationToken ct, int maxTokens = 3000) =>
        (await CallAsync(ctx, agent, Prompts.System(role), instructions, context, json: false, maxTokens, ct)).Content;

    private Task<LlmResponse> CallAsync(NodeContext ctx, string agent, string system, string instructions, string context, bool json, int maxTokens, CancellationToken ct)
    {
        var user = instructions.Trim() + "\n\n" + context.Trim();
        if (!ctx.IsFallback) user = Prompts.WithFeedback(user, ctx.Feedback);
        return llm.CompleteAsync(new LlmRequest
        {
            Agent = agent,
            SystemPrompt = system,
            UserPrompt = user,
            JsonOutput = json,
            MaxTokens = maxTokens,
            RunId = ctx.Run.RunId,
            NodeId = ctx.Node.Id,
            PreferReplay = ctx.IsFallback,
        }, ct);
    }

    public static string ExtractCode(string llmOutput)
    {
        var match = Regex.Match(llmOutput, "```[a-zA-Z#]*\\s*\\n(?<code>[\\s\\S]*?)```");
        var code = match.Success ? match.Groups["code"].Value : llmOutput;
        return code.Replace("\r\n", "\n").Trim() + "\n";
    }

    public static T Read<T>(RunContext run, string key) => JsonText.Deserialize<T>(run.Require(key));

    public static T? TryRead<T>(RunContext run, string key) where T : class =>
        run.Get(key) is { } json ? JsonText.Deserialize<T>(json) : null;
}

public static class Setting
{
    public const string Project = "project";
    public const string ForceNew = "new";
    public const string Deploy = "deploy";
    public const string Load = "load";
    public const string Availability = "availability";
    public const string ReadWriteRatio = "readWriteRatio";
    public const string RetentionDays = "retentionDays";
}

/// <summary>Reads/writes files of the selected project (paths relative to the project root, '/' separators).</summary>
public static class ProjectFiles
{
    private static readonly string[] Roots = ["src/App/Features", "tests/App.Tests/Features"];

    public static IReadOnlyList<string> FeatureFiles(string projectDir) =>
        Roots.Select(r => Path.Combine(projectDir, r))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(projectDir, f).Replace('\\', '/'))
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    public static string? Read(string projectDir, string relativePath)
    {
        var full = Path.Combine(projectDir, relativePath);
        return File.Exists(full) ? File.ReadAllText(full).Replace("\r\n", "\n") : null;
    }

    public static async Task WriteAsync(string projectDir, string relativePath, string content, CancellationToken ct)
    {
        var full = Path.GetFullPath(Path.Combine(projectDir, relativePath));
        if (!full.StartsWith(Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase))
            throw new PolicyViolationException($"Refusing to write outside the project: {relativePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct);
    }

    /// <summary>Endpoint routes discovered from code (Map{Verb}(...)), used for project cards and docs.</summary>
    public static List<string> DiscoverEndpoints(string projectDir)
    {
        var endpoints = new List<string>();
        foreach (var file in FeatureFiles(projectDir).Where(f => f.StartsWith("src/")))
        {
            var text = Read(projectDir, file) ?? "";
            var group = Regex.Match(text, "MapGroup\\(\"(?<g>[^\"]+)\"\\)").Groups["g"].Value;
            foreach (Match m in Regex.Matches(text, "(?<recv>\\w+)\\.Map(?<verb>Get|Post|Put|Delete|Patch)\\(\"(?<route>[^\"]*)\""))
            {
                var route = m.Groups["route"].Value;
                var prefix = m.Groups["recv"].Value == "api" && group.Length > 0 ? group.TrimEnd('/') : "";
                var full = (prefix + "/" + route.TrimStart('/')).TrimEnd('/');
                full = Regex.Replace(full, "\\{(\\w+):[^/]*\\}", "{$1}"); // strip route constraints
                endpoints.Add($"{m.Groups["verb"].Value.ToUpperInvariant()} {(full.Length == 0 ? "/" : full)}");
            }
        }
        return endpoints.Distinct().ToList();
    }
}
