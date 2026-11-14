using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Search;

namespace PriorAuthBot.Core.Retrieval.Keyword;

/// <summary>
/// BM25 over the in-memory store, for development and tests. The store has no keyword search of its
/// own, so this asks it for the chunks the caller may see (the group filter runs in the store's
/// query, as always) and ranks them with <see cref="Bm25Index{T}"/>. It reads every visible chunk on
/// every query, which is fine for the sample documents and nothing else.
/// </summary>
public sealed class InMemoryKeywordSearch(VectorStoreCollection<Guid, ChunkRecord> collection) : IKeywordSearch
{
    private const int MaxChunks = 50_000;

    public string Name => "BM25 (in memory)";

    public async Task<IReadOnlyList<ChunkRecord>> SearchAsync(string query, IReadOnlyList<string> groups, int top, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0 || !await collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var list = groups.ToList();
        var index = new Bm25Index<ChunkRecord>();
        await foreach (var record in collection.GetAsync(r => r.AllowedGroups.Any(g => list.Contains(g)), MaxChunks, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            index.Add(record, record.SearchText);
        }

        return index.Search(query, top).Select(r => r.Item).ToList();
    }
}
