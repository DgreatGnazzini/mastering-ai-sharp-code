namespace PriorAuthBot.Core.Search;

/// <summary>An item after fusion: its fused score and its 1-based rank in each input list (null when absent).</summary>
public sealed record FusedItem<T>(T Item, double Score, IReadOnlyList<int?> Ranks);

/// <summary>
/// Reciprocal rank fusion (chapter 8): merges ranked lists by position, not by score. Each list
/// adds <c>1 / (k + rank)</c> for every item in it, so an item near the top of two lists beats one at
/// the top of a single list. Positions are all it uses, which is the point: a cosine similarity
/// of 0.71 and a BM25 score of 14.2 are not on the same scale and cannot be added, but "second" and
/// "fifth" can. k = 60 comes from the original paper (Cormack, Clarke, and Buettcher, 2009) and is
/// what Azure AI Search, Elasticsearch, and the pgvector connector use.
/// </summary>
public static class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    public static IReadOnlyList<FusedItem<T>> Fuse<T, TKey>(IReadOnlyList<IReadOnlyList<T>> rankings, Func<T, TKey> key, int k = DefaultK)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(k);

        var fused = new Dictionary<TKey, (T Item, double Score, int?[] Ranks)>();
        for (var list = 0; list < rankings.Count; list++)
        {
            var ranking = rankings[list];
            for (var position = 0; position < ranking.Count; position++)
            {
                var item = ranking[position];
                var id = key(item);
                if (!fused.TryGetValue(id, out var entry))
                {
                    entry = (item, 0, new int?[rankings.Count]);
                }

                // An item listed twice in one list counts once, at its best position.
                if (entry.Ranks[list] is null)
                {
                    entry.Ranks[list] = position + 1;
                    entry.Score += 1.0 / (k + position + 1);
                }

                fused[id] = entry;
            }
        }

        return fused.Values
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Ranks.Min(r => r ?? int.MaxValue))
            .Select(e => new FusedItem<T>(e.Item, e.Score, e.Ranks))
            .ToList();
    }
}
