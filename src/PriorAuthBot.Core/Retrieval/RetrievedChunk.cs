namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// A chunk returned by <see cref="SecureRetriever"/>. <see cref="Score"/> is what the results are
/// ordered by: the cosine similarity in vector mode, the fused score in hybrid mode, or the
/// reranker's score when one ran. The other scores show where the chunk came from (chapter 8).
/// </summary>
public sealed record RetrievedChunk(ChunkRecord Record, double Score)
{
    /// <summary>Cosine similarity, when the vector side found the chunk.</summary>
    public double? VectorScore { get; init; }

    /// <summary>1-based rank on the keyword side, when keyword search found the chunk.</summary>
    public int? KeywordRank { get; init; }

    /// <summary>The reranker's score, when reranking ran.</summary>
    public double? RerankScore { get; init; }
}
