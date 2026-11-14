namespace PriorAuthBot.Core.Rag;

/// <summary>PriorAuthBot's answer to one question.</summary>
/// <param name="Answer">The answer text, with [n] markers that refer to <paramref name="Sources"/>.</param>
/// <param name="Declined">True when PriorAuthBot said it does not know.</param>
/// <param name="Sources">The sources the answer was given, numbered as in the answer. Always ones the caller can see.</param>
public sealed record RagAnswer(string Answer, bool Declined, IReadOnlyList<SourceReference> Sources)
{
    /// <summary>The sources the answer actually cites (chapter 8). This is what the UI shows.</summary>
    public IReadOnlyList<SourceReference> Citations => [.. Sources.Where(s => s.Cited)];
}

/// <summary>
/// A structured citation (chapter 8): from the chunk ID down to the document, section, page, and a
/// link. <paramref name="Link"/> points at PriorAuthBot's own <c>/documents/{id}</c> endpoint, which
/// checks the caller's groups again on every request, so a copied link opens for nobody else.
/// </summary>
/// <param name="Number">The [n] the answer uses.</param>
/// <param name="ChunkId">The chunk the cited text came from, as <c>{DocumentId}:{ChunkIndex}</c>.</param>
/// <param name="Page">The page the chunk starts on, for PDFs and scans; null otherwise.</param>
/// <param name="Cited">True when the answer cites this source with a [n] marker.</param>
public sealed record SourceReference(
    int Number,
    string DocumentId,
    string Title,
    string? PolicyNumber,
    string Section,
    string ChunkId = "",
    int? Page = null,
    string Link = "",
    bool Cited = false);
