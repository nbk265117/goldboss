namespace DofusCompanion.Application.Matching;

/// <summary>
/// String similarity measures on already-normalized text. All scores are in [0, 1] and mean
/// "1 - (edit operations needed / length of the longer string)". They are NOT probabilities.
/// </summary>
public static class StringSimilarity
{
    /// <summary>Optimal string alignment distance (Levenshtein + adjacent transpositions).</summary>
    public static int EditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var cols = b.Length + 1;
        // Three rolling rows are enough for the transposition rule.
        Span<int> prevPrev = cols <= 256 ? stackalloc int[cols] : new int[cols];
        Span<int> prev = cols <= 256 ? stackalloc int[cols] : new int[cols];
        Span<int> cur = cols <= 256 ? stackalloc int[cols] : new int[cols];

        for (var j = 0; j < cols; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j < cols; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prevPrev[j - 2] + 1);
                cur[j] = v;
            }

            var tmp = prevPrev;
            prevPrev = prev;
            prev = cur;
            cur = tmp;
        }

        return prev[b.Length];
    }

    /// <summary>
    /// Edit distance capped at <paramref name="max"/>: returns max + 1 as soon as the distance is known to
    /// exceed it. Much cheaper than a full computation when most comparisons are far apart.
    /// </summary>
    public static int BoundedEditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);

        var cols = b.Length + 1;
        Span<int> prev = cols <= 256 ? stackalloc int[cols] : new int[cols];
        Span<int> cur = cols <= 256 ? stackalloc int[cols] : new int[cols];
        for (var j = 0; j < cols; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j < cols; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }

            if (rowMin > max) return max + 1;
            var tmp = prev;
            prev = cur;
            cur = tmp;
        }

        return Math.Min(prev[b.Length], max + 1);
    }

    /// <summary>Ratio if it is at least <paramref name="minRatio"/>, otherwise 0. Uses the bounded distance.</summary>
    public static double RatioAtLeast(string a, string b, double minRatio)
    {
        var len = Math.Max(a.Length, b.Length);
        if (len == 0) return 1.0;
        var maxEdits = (int)Math.Floor((1 - minRatio) * len);
        var d = BoundedEditDistance(a, b, maxEdits);
        return d > maxEdits ? 0 : 1.0 - (double)d / len;
    }

    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var max = Math.Max(a.Length, b.Length);
        return 1.0 - (double)EditDistance(a, b) / max;
    }

    /// <summary>Ratio after sorting tokens, so word order does not matter ("geant kralamoure" ~ "kralamoure geant").</summary>
    public static double TokenSortRatio(string a, string b)
    {
        static string Sorted(string s)
        {
            var t = TextNormalizer.Tokenize(s);
            Array.Sort(t, StringComparer.Ordinal);
            return string.Join(' ', t);
        }

        return Ratio(Sorted(a), Sorted(b));
    }

    /// <summary>Best of plain and token-sorted ratio. This is the single score used for entity identification.</summary>
    public static double Score(string normalizedQuery, string normalizedName)
    {
        if (normalizedQuery.Length == 0 || normalizedName.Length == 0) return 0;
        if (string.Equals(normalizedQuery, normalizedName, StringComparison.Ordinal)) return 1.0;
        return Math.Max(Ratio(normalizedQuery, normalizedName), TokenSortRatio(normalizedQuery, normalizedName));
    }
}
