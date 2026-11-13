namespace PriorAuthBot.Core.Ingestion;

public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    /// <summary>Upper bound per chunk. Chapter 7 measures 200, 350, and 600 against the golden set.</summary>
    public int MaxTokens { get; set; } = 350;

    /// <summary>Tokens repeated from the end of one chunk at the start of the next, when a section must be split.</summary>
    public int OverlapTokens { get; set; } = 50;
}
