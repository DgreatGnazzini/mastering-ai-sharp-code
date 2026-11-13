using PriorAuthBot.Core.Documents;

namespace PriorAuthBot.Core.Ingestion;

public interface IChunker
{
    string Name { get; }

    IReadOnlyList<TextChunk> Chunk(SourceDocument document);
}
