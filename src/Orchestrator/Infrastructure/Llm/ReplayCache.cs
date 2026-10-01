using System.Text;
using Orchestrator.Core.Common;
using Orchestrator.Core.Llm;

namespace Orchestrator.Infrastructure.Llm;

/// <summary>
/// Content-addressed store of LLM responses keyed by hash(agent + system prompt + user prompt).
/// <list type="bullet">
/// <item><c>golden.json</c> curated, human-reviewed responses committed to the repo: { "agent-key": "response" }.</item>
/// <item><c>recorded/</c>   responses captured from live runs (one text file each, git-ignored).</item>
/// <item><c>pending/</c>    prompts that missed the cache in replay mode (to author new golden entries).</item>
/// </list>
/// </summary>
public sealed class ReplayCache(string directory)
{
    private const string Separator = "=====RESPONSE=====";
    private Dictionary<string, string>? _golden;

    public string Directory { get; } = Path.GetFullPath(directory);

    public string GoldenFile => Path.Combine(Directory, "golden.json");

    public static string KeyFor(LlmRequest r) =>
        Hashing.Short($"{r.Agent}\n{r.SystemPrompt}\n{r.UserPrompt}", 16);

    public IReadOnlyDictionary<string, string> Golden => _golden ??= File.Exists(GoldenFile)
        ? JsonText.Deserialize<Dictionary<string, string>>(File.ReadAllText(GoldenFile))
        : [];

    public void AddGolden(string agentKey, string response)
    {
        var all = new SortedDictionary<string, string>(Golden.ToDictionary(), StringComparer.Ordinal) { [agentKey] = response };
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(GoldenFile, JsonText.Serialize(all));
        _golden = null;
    }

    public string? TryGet(LlmRequest request, out string key, out string? tier)
    {
        key = KeyFor(request);
        if (Golden.TryGetValue($"{request.Agent}-{key}", out var golden))
        {
            tier = "golden";
            return golden;
        }
        foreach (var t in new[] { "recorded" })
        {
            var file = PathFor(t, request.Agent, key);
            if (!File.Exists(file)) continue;
            var text = File.ReadAllText(file);
            var idx = text.IndexOf(Separator, StringComparison.Ordinal);
            if (idx < 0) continue;
            tier = t;
            return text[(idx + Separator.Length)..].TrimStart('\r', '\n');
        }
        tier = null;
        return null;
    }

    public void Record(LlmRequest request, LlmResponse response)
    {
        var file = PathFor("recorded", request.Agent, response.CacheKey);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Header(request, response.CacheKey, response.Model) + Separator + "\n" + response.Content);
    }

    public string WritePending(LlmRequest request)
    {
        var key = KeyFor(request);
        var file = PathFor("pending", request.Agent, key);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Header(request, key, "n/a") + "## SYSTEM\n" + request.SystemPrompt + "\n\n## USER\n" + request.UserPrompt + "\n");
        return file;
    }

    private string PathFor(string tier, string agent, string key) => Path.Combine(Directory, tier, $"{agent}-{key}.txt");

    private static string Header(LlmRequest r, string key, string model)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"agent: {r.Agent}");
        sb.AppendLine($"key: {key}");
        sb.AppendLine($"node: {r.NodeId}");
        sb.AppendLine($"model: {model}");
        sb.AppendLine($"recorded: {DateTimeOffset.UtcNow:O}");
        return sb.ToString();
    }
}
