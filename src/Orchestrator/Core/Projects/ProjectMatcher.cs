using Orchestrator.Core.Common;

namespace Orchestrator.Core.Projects;

public enum ChangeIntent { New, Change, Unclear }

public sealed record ProjectCandidate(ProjectCard Card, double Score, IReadOnlyList<string> MatchedTerms);

/// <summary>Pluggable retrieval step (lexical today; embeddings could implement the same interface).</summary>
public interface IProjectMatcher
{
    IReadOnlyList<ProjectCandidate> FindCandidates(string requirement, IReadOnlyList<ProjectCard> cards, int top = 3);
}

/// <summary>
/// Weighted lexical overlap between the request and each card's fields. Deterministic, millisecond-fast,
/// and explainable (returns the matched terms, which are shown to the human and written to the audit log).
/// </summary>
public sealed class LexicalProjectMatcher : IProjectMatcher
{
    public IReadOnlyList<ProjectCandidate> FindCandidates(string requirement, IReadOnlyList<ProjectCard> cards, int top = 3)
    {
        var query = Tokenizer.Terms(requirement);
        query.ExceptWith(IntentClassifier.IntentWords);
        if (query.Count == 0) return [];

        var results = new List<ProjectCandidate>();
        foreach (var card in cards)
        {
            var fields = new (double Weight, string Text)[]
            {
                (3.0, card.Name + " " + card.Slug.Replace('-', ' ') + " " + string.Join(' ', card.Aliases)),
                (2.0, string.Join(' ', card.Capabilities)),
                (1.5, string.Join(' ', card.Entities) + " " + string.Join(' ', card.Endpoints)),
                (1.0, card.Summary),
            };

            double score = 0;
            var matched = new HashSet<string>();
            foreach (var term in query)
            {
                var best = 0.0;
                foreach (var (weight, text) in fields)
                    if (Tokenizer.Terms(text).Contains(term)) best = Math.Max(best, weight);
                if (best > 0) { score += best; matched.Add(term); }
            }

            // Normalise: 1.0 ≈ every query term matched a high-weight field.
            var normalised = Math.Min(1.0, score / (Math.Max(2, query.Count) * 2.0));
            if (normalised > 0) results.Add(new ProjectCandidate(card, Math.Round(normalised, 3), matched.ToList()));
        }
        return results.OrderByDescending(r => r.Score).Take(top).ToList();
    }
}

public static class IntentClassifier
{
    private static readonly string[] ChangeVerbs =
        ["add", "fix", "change", "update", "extend", "modify", "remove", "enhance", "improve", "refactor", "support", "allow", "existing", "bug", "make"];

    private static readonly string[] NewVerbs = ["build", "create", "develop", "implement", "new", "scratch", "greenfield"];

    public static readonly HashSet<string> IntentWords = ChangeVerbs.Concat(NewVerbs).Select(Tokenizer.Stem).ToHashSet();

    public static ChangeIntent Classify(string requirement)
    {
        var words = Tokenizer.Words(requirement).Take(6).ToList(); // the leading verb carries intent
        var change = words.Count(ChangeVerbs.Contains);
        var create = words.Count(NewVerbs.Contains);
        if (change > create) return ChangeIntent.Change;
        if (create > change) return ChangeIntent.New;
        return ChangeIntent.Unclear;
    }
}

public enum RoutingKind { Greenfield, Brownfield, AskHuman }

public sealed record RoutingDecision(RoutingKind Kind, string? ProjectSlug, double Confidence, string Rationale, IReadOnlyList<ProjectCandidate> Candidates);

/// <summary>Deterministic routing rules applied after (optional) LLM re-ranking.</summary>
public static class RoutingRules
{
    public const double HighConfidence = 0.8;
    public const double LowConfidence = 0.5;

    public static RoutingDecision Decide(ChangeIntent intent, IReadOnlyList<ProjectCandidate> candidates, double? llmConfidence = null, string? llmChoice = null)
    {
        var top = candidates.FirstOrDefault();
        var second = candidates.Skip(1).FirstOrDefault();
        var confidence = llmConfidence ?? top?.Score ?? 0;
        var slug = llmChoice ?? top?.Card.Slug;

        if (top is null || confidence < LowConfidence)
        {
            return intent == ChangeIntent.Change
                ? new(RoutingKind.AskHuman, null, confidence, "Request reads like a change, but no existing project matches.", candidates)
                : new(RoutingKind.Greenfield, null, 1 - confidence, "No existing project matches; treating as a new project.", candidates);
        }

        var ambiguous = second is not null && top.Score - second.Score < 0.15;
        if (confidence >= HighConfidence && !ambiguous)
            return new(RoutingKind.Brownfield, slug, confidence, $"Strong match on: {string.Join(", ", top.MatchedTerms)}.", candidates);

        return new(RoutingKind.AskHuman, slug, confidence,
            ambiguous ? "Several projects match similarly." : "Partial match; human confirmation required.", candidates);
    }
}
