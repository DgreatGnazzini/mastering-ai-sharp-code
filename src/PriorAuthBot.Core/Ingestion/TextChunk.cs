namespace PriorAuthBot.Core.Ingestion;

/// <summary>A piece of a document produced by an <see cref="IChunker"/>.</summary>
/// <param name="Index">Position within the document, from 0.</param>
/// <param name="Section">Heading path, such as "Accrual &gt; Carryover". Empty for fixed-size chunks.</param>
/// <param name="Text">The chunk's own text, shown to the model and the user.</param>
/// <param name="TokenCount">Token count of <paramref name="Text"/>.</param>
public sealed record TextChunk(int Index, string Section, string Text, int TokenCount);
