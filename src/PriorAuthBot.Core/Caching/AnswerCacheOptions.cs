namespace PriorAuthBot.Core.Caching;

public sealed class AnswerCacheOptions
{
    public const string SectionName = "AnswerCache";

    /// <summary>Off by default, like every cache since chapter 2: it holds questions and answers, which can hold PHI.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Cosine similarity at which a new question counts as the same question as a cached one. 1.0
    /// means exact repeats only. Chapter 13's eval measures paraphrases and near-misses at several
    /// thresholds for the configured embedding model; pick from that table, not from intuition.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.97;

    /// <summary>How long an answer is reused. Ingestion clears the cache when documents change, so this bounds everything else.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(4);

    /// <summary>The most answers kept per permission set. The oldest go first.</summary>
    public int MaxEntriesPerPartition { get; set; } = 500;
}
