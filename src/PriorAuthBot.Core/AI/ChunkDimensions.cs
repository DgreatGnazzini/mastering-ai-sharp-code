namespace PriorAuthBot.Core.AI;

/// <summary>The embedding size every provider is configured to return (chapter 5).</summary>
public static class ChunkDimensions
{
    /// <summary>
    /// 768 is native for nomic-embed-text. OpenAI's text-embedding-3 models can shorten their
    /// output to 768 on request, so the vector column never changes when the provider does.
    /// </summary>
    public const int Size = 768;
}
