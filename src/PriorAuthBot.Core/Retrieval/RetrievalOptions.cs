namespace PriorAuthBot.Core.Retrieval;

/// <summary>Vector only (chapters 6 and 7), or vector and keyword search fused by rank (chapter 8).</summary>
public enum RetrievalMode
{
    Vector,
    Hybrid,
}

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>How many chunks to return to the context assembler, after fusion and reranking.</summary>
    public int Top { get; set; } = 6;

    /// <summary>
    /// Chunks scoring below this cosine similarity are treated as not relevant. The right value
    /// depends on the embedding model, so it is measured with the golden set, not guessed (chapter 7).
    /// In hybrid mode it still gates the vector side.
    /// </summary>
    public double MinScore { get; set; } = 0.6;

    public RetrievalMode Mode { get; set; } = RetrievalMode.Hybrid;

    /// <summary>How many candidates each side (vector, keyword) contributes before fusion and reranking.</summary>
    public int Candidates { get; set; } = 20;

    /// <summary>The k in reciprocal rank fusion's 1 / (k + rank).</summary>
    public int RrfK { get; set; } = Search.ReciprocalRankFusion.DefaultK;

    /// <summary>
    /// The keyword side's relevance gate: the share of the query's terms a chunk must contain. Keyword
    /// search returns anything that shares one word with the query, so without a gate "When must a
    /// sepsis screen be escalated to a physician?" pulls in any chunk that says "physician" (section 8.3).
    /// </summary>
    public double MinTermCoverage { get; set; } = 0.5;

    public RerankOptions Rerank { get; set; } = new();

    public QueryRewriteOptions QueryRewrite { get; set; } = new();
}

/// <summary>Reranking the fused candidates (section 8.4). Off by default; turn it on after measuring it.</summary>
public sealed class RerankOptions
{
    /// <summary><c>None</c>, <c>CrossEncoder</c> (a local ONNX model), or <c>Llm</c> (the chat model).</summary>
    public string Provider { get; set; } = "None";

    /// <summary>How many fused candidates to rerank. The rest keep their fused order behind them.</summary>
    public int Candidates { get; set; } = 10;

    /// <summary>
    /// Candidates the reranker scores below this are dropped. Its scale depends on the reranker: a
    /// cross-encoder's raw logit, or the LLM reranker's 0 to 3. Measured with the golden set: on the
    /// chapter 8 run the cross-encoder scored every expected document at 0.87 or more and everything
    /// returned for a decline question at -4.65 or less, so appsettings.json sets 0 (section 8.4).
    /// </summary>
    public double? MinScore { get; set; }

    /// <summary>The cross-encoder's folder: <c>model.onnx</c> and <c>vocab.txt</c>. See scripts/get-reranker.sh.</summary>
    public string ModelPath { get; set; } = "../../models/ms-marco-MiniLM-L6-v2";
}

/// <summary>Query rewriting and multi-query retrieval (section 8.5). Off by default: it costs one model call per question.</summary>
public sealed class QueryRewriteOptions
{
    public bool Enabled { get; set; }

    /// <summary>How many alternative phrasings to ask for. The original question is always searched too.</summary>
    public int Count { get; set; } = 3;
}
