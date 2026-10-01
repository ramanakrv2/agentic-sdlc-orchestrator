using System.Text;

namespace Orchestrator.Core.Common;

public static class Slug
{
    private static readonly HashSet<string> Noise =
    [
        "a", "an", "the", "build", "create", "make", "new", "service", "app", "application",
        "system", "with", "and", "for", "that", "please", "api", "simple", "basic", "i", "want", "need", "to",
    ];

    /// <summary>Derives a short, filesystem-safe project slug from a project name or requirement.</summary>
    public static string From(string text, int maxWords = 3)
    {
        var words = Tokenizer.Words(text).Where(w => !Noise.Contains(w)).Take(maxWords).ToList();
        if (words.Count == 0) words.Add("project");
        var sb = new StringBuilder(string.Join('-', words));
        return sb.Length > 40 ? sb.ToString(0, 40).TrimEnd('-') : sb.ToString();
    }
}

public static class Tokenizer
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "to", "of", "and", "or", "for", "in", "on", "with", "that", "this", "it", "is", "be",
        "should", "must", "can", "we", "i", "our", "please", "so", "as", "by", "at", "from", "into", "all",
    ];

    public static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); continue; }
            if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    public static HashSet<string> Terms(string text) =>
        Words(text).Where(w => w.Length > 1 && !StopWords.Contains(w)).Select(Stem).ToHashSet();

    /// <summary>Very light stemming so "links"/"link" and "shortener"/"shorten" match.</summary>
    public static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 5 && w.EndsWith("ener")) return w[..^2];
        if (w.Length > 4 && w.EndsWith("es") && !w.EndsWith("ses")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) return w[..^1];
        return w;
    }
}
