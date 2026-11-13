namespace PriorAuthBot.Core.Retrieval;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>How many chunks to fetch from the store before the context assembler trims them.</summary>
    public int Top { get; set; } = 6;

    /// <summary>
    /// Chunks scoring below this cosine similarity are treated as not relevant. The right value
    /// depends on the embedding model, so it is measured with the golden set, not guessed (chapter 7).
    /// </summary>
    public double MinScore { get; set; } = 0.5;
}
