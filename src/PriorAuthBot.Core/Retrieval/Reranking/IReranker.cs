namespace PriorAuthBot.Core.Retrieval.Reranking;

/// <summary>
/// Scores how well each passage answers a query (section 8.4). A reranker reads the query and the
/// passage together, which is what makes it more accurate than comparing two vectors made
/// separately, and what makes it too slow to run over a whole index: it only ever sees the few
/// candidates that retrieval already found.
/// </summary>
public interface IReranker
{
    string Name { get; }

    /// <summary>One score per passage, in the same order. Higher is more relevant.</summary>
    Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default);
}
