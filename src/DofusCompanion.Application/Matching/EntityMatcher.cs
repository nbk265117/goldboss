using DofusCompanion.Domain;

namespace DofusCompanion.Application.Matching;

public sealed record MatchingOptions
{
    /// <summary>Candidates scoring below this are discarded.</summary>
    public double MinCandidateScore { get; init; } = 0.75;
    /// <summary>A candidate at or above this score, with a clear margin over the runner-up, is auto-accepted (still correctable).</summary>
    public double AutoAcceptScore { get; init; } = 0.92;
    /// <summary>If the two best distinct entities are closer than this, the result is ambiguous and needs confirmation.</summary>
    public double AmbiguityMargin { get; init; } = 0.05;
    /// <summary>Names shorter than this (normalized) are only matched exactly, to avoid spurious fuzzy hits.</summary>
    public int MinFuzzyLength { get; init; } = 4;
    public int MaxCandidates { get; init; } = 5;
}

public enum MatchOutcome
{
    /// <summary>No entity scored above the minimum.</summary>
    NoMatch,
    /// <summary>Several entities are similarly plausible; the user must choose.</summary>
    Ambiguous,
    /// <summary>One plausible entity, but its score is below the auto-accept threshold; the user should confirm.</summary>
    NeedsConfirmation,
    /// <summary>One clear winner above the auto-accept threshold.</summary>
    Confident,
}

/// <summary>A candidate entity for a piece of text. <see cref="Score"/> is a normalized edit similarity, not a probability.</summary>
public sealed record MatchCandidate(GameEntity Entity, double Score, string MatchedName, string MatchedText);

public sealed record MatchResult(MatchOutcome Outcome, IReadOnlyList<MatchCandidate> Candidates)
{
    public MatchCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
    public static MatchResult None { get; } = new(MatchOutcome.NoMatch, []);
}

/// <summary>
/// Identifies entities from free text (user query or OCR output). Uses a character-trigram index to
/// pre-select plausible names, then ranks them by <see cref="StringSimilarity.Score"/>.
/// Immutable after construction; rebuild when the knowledge base changes.
/// </summary>
public sealed class EntityMatcher
{
    private readonly MatchingOptions _options;
    private readonly List<IndexedName> _names = [];
    private readonly Dictionary<string, List<int>> _trigramIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _exact = new(StringComparer.Ordinal);

    private sealed record IndexedName(GameEntity Entity, string Display, string Normalized, int TokenCount, int TrigramCount);

    public EntityMatcher(IEnumerable<GameEntity> entities, MatchingOptions? options = null)
    {
        _options = options ?? new MatchingOptions();
        foreach (var entity in entities)
        {
            foreach (var name in entity.AllNames().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = TextNormalizer.Normalize(name);
                if (normalized.Length == 0) continue;

                var trigrams = Trigrams(normalized);
                var idx = _names.Count;
                _names.Add(new IndexedName(entity, name, normalized, TextNormalizer.Tokenize(normalized).Length, trigrams.Count));

                if (!_exact.TryGetValue(normalized, out var exactList))
                    _exact[normalized] = exactList = [];
                exactList.Add(idx);

                foreach (var t in trigrams)
                {
                    if (!_trigramIndex.TryGetValue(t, out var list))
                        _trigramIndex[t] = list = [];
                    list.Add(idx);
                }
            }
        }
    }

    public MatchingOptions Options => _options;
    public int IndexedNameCount => _names.Count;

    /// <summary>Matches a single piece of text (a query or one OCR line) against entity names.</summary>
    public MatchResult Match(string text, Func<GameEntity, bool>? filter = null)
    {
        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0) return MatchResult.None;

        var best = new Dictionary<string, MatchCandidate>(StringComparer.Ordinal);
        ScoreAgainst(normalized, text, best, filter);
        return Classify(best.Values);
    }

    /// <summary>
    /// Finds entity names inside longer OCR text. Every line is scanned with token windows of roughly the
    /// size of each candidate name, so "Kralamoure Géant (Niv. 1000)" still yields "Kralamoure Géant".
    /// Returns one ranked candidate list per entity found, best first.
    /// </summary>
    public IReadOnlyList<MatchCandidate> FindInText(IEnumerable<string> lines, Func<GameEntity, bool>? filter = null)
    {
        var best = new Dictionary<string, MatchCandidate>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var tokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(line));
            if (tokens.Length == 0) continue;

            var maxWindow = Math.Min(tokens.Length, 8);
            for (var size = 1; size <= maxWindow; size++)
            {
                for (var start = 0; start + size <= tokens.Length; start++)
                {
                    var window = string.Join(' ', tokens, start, size);
                    ScoreAgainst(window, line, best, filter, requiredTokenCount: size);
                }
            }
        }

        return best.Values
            .Where(c => c.Score >= _options.MinCandidateScore)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Entity.CanonicalName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Classifies a ranked candidate set into an outcome using the configured thresholds.</summary>
    public MatchResult Classify(IEnumerable<MatchCandidate> candidates)
    {
        var ranked = candidates
            .Where(c => c.Score >= _options.MinCandidateScore)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Entity.CanonicalName, StringComparer.CurrentCulture)
            .Take(_options.MaxCandidates)
            .ToList();

        if (ranked.Count == 0) return MatchResult.None;

        var top = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1] : null;

        // Two different entities with (nearly) the same score: never pick one silently.
        if (runnerUp is not null && top.Score - runnerUp.Score < _options.AmbiguityMargin)
            return new MatchResult(MatchOutcome.Ambiguous, ranked);

        return top.Score >= _options.AutoAcceptScore
            ? new MatchResult(MatchOutcome.Confident, ranked)
            : new MatchResult(MatchOutcome.NeedsConfirmation, ranked);
    }

    private void ScoreAgainst(string normalized, string original, Dictionary<string, MatchCandidate> best,
        Func<GameEntity, bool>? filter, int? requiredTokenCount = null)
    {
        foreach (var idx in CandidateIndexes(normalized))
        {
            var name = _names[idx];

            // When scanning OCR windows only compare windows of about the same word count as the name.
            if (requiredTokenCount is { } n && Math.Abs(name.TokenCount - n) > 1)
                continue;
            if (filter is not null && !filter(name.Entity))
                continue;

            double score;
            if (string.Equals(name.Normalized, normalized, StringComparison.Ordinal))
                score = 1.0;
            else if (name.Normalized.Length < _options.MinFuzzyLength || normalized.Length < _options.MinFuzzyLength)
                continue;
            else
                score = StringSimilarity.Score(normalized, name.Normalized);

            if (score < _options.MinCandidateScore)
                continue;

            if (!best.TryGetValue(name.Entity.Id, out var existing) || existing.Score < score)
                best[name.Entity.Id] = new MatchCandidate(name.Entity, score, name.Display, original);
        }
    }

    private IEnumerable<int> CandidateIndexes(string normalized)
    {
        if (_exact.TryGetValue(normalized, out var exact))
            foreach (var i in exact)
                yield return i;

        if (normalized.Length < _options.MinFuzzyLength)
            yield break;

        // Count shared trigrams; a name can only reach MinCandidateScore if it shares a reasonable fraction.
        var trigrams = Trigrams(normalized);
        var counts = new Dictionary<int, int>();
        foreach (var t in trigrams)
        {
            if (!_trigramIndex.TryGetValue(t, out var list)) continue;
            foreach (var i in list)
                counts[i] = counts.GetValueOrDefault(i) + 1;
        }

        foreach (var (i, shared) in counts)
        {
            var denominator = Math.Max(trigrams.Count, _names[i].TrigramCount);
            // Loose prefilter (well below the score threshold) so the exact score decides.
            if (denominator > 0 && (double)shared / denominator >= 0.25)
                yield return i;
        }
    }

    private static HashSet<string> Trigrams(string normalized)
    {
        var padded = $"  {normalized} ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= padded.Length; i++)
            set.Add(padded.Substring(i, 3));
        return set;
    }
}
