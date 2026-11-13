namespace PriorAuthBot.Core.Rag;

public sealed class ContextOptions
{
    public const string SectionName = "Context";

    /// <summary>Token budget for the sources block. The system prompt and question come on top.</summary>
    public int MaxContextTokens { get; set; } = 1500;

    /// <summary>Keeps one long document from crowding out every other source.</summary>
    public int MaxChunksPerDocument { get; set; } = 2;
}
