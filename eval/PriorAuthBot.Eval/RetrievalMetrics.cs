namespace PriorAuthBot.Eval;

/// <summary>
/// Retrieval metrics (chapter 9), over documents rather than chunks: a question is answered by a
/// policy, and three chunks of the right policy count once. <c>ranked</c> is the distinct document
/// IDs in the order retrieval returned them.
/// </summary>
internal static class RetrievalMetrics
{
    /// <summary>The share of the expected documents found in the top <paramref name="k"/>. 1 when nothing is expected.</summary>
    public static double RecallAtK(IReadOnlyList<string> ranked, IReadOnlyCollection<string> expected, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count == 0)
        {
            return 1;
        }

        return (double)expected.Count(e => ranked.Take(k).Contains(e)) / expected.Count;
    }

    /// <summary>1 / the rank of the first expected document, or 0 when none was retrieved.</summary>
    public static double ReciprocalRank(IReadOnlyList<string> ranked, IReadOnlyCollection<string> expected)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(expected);
        for (var i = 0; i < ranked.Count; i++)
        {
            if (expected.Contains(ranked[i]))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    /// <summary>Distinct document IDs, in the order of their best-ranked chunk.</summary>
    public static IReadOnlyList<string> Documents(IEnumerable<string> chunkDocumentIds) => chunkDocumentIds.Distinct().ToList();
}
