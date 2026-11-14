namespace PriorAuthBot.Core.Retrieval.Keyword;

/// <summary>
/// The keyword side of hybrid retrieval (chapter 8). Like the vector side, every implementation
/// takes the caller's groups and applies them inside the query, so a keyword match in a document
/// the caller cannot open is never returned, counted, or ranked.
/// </summary>
public interface IKeywordSearch
{
    /// <summary>A short name for logs and reports, such as "BM25 (in memory)".</summary>
    string Name { get; }

    /// <summary>Chunks matching any term of <paramref name="query"/>, best first.</summary>
    Task<IReadOnlyList<ChunkRecord>> SearchAsync(string query, IReadOnlyList<string> groups, int top, CancellationToken cancellationToken = default);
}

/// <summary>For stores with no keyword search wired up yet. Hybrid retrieval then runs the vector side alone.</summary>
public sealed class NoKeywordSearch : IKeywordSearch
{
    public string Name => "none";

    public Task<IReadOnlyList<ChunkRecord>> SearchAsync(string query, IReadOnlyList<string> groups, int top, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChunkRecord>>([]);
}
