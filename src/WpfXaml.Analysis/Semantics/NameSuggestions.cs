namespace WpfXaml.Analysis.Semantics;

/// <summary>"Did you mean ...?" suggestions based on edit distance.</summary>
internal static class NameSuggestions
{
    public static string? Closest(string name, IEnumerable<string> candidates)
    {
        var maxDistance = Math.Max(1, name.Length / 3);
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (Math.Abs(candidate.Length - name.Length) > maxDistance)
            {
                continue;
            }

            var distance = Distance(name, candidate);
            if (distance < bestDistance || (distance == bestDistance && best is not null && string.CompareOrdinal(candidate, best) < 0))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return bestDistance <= maxDistance ? best : null;
    }

    public static string DidYouMean(string? suggestion) => suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?";

    /// <summary>Case-insensitive optimal string alignment distance (Levenshtein plus adjacent transpositions).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2])
                    && char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
