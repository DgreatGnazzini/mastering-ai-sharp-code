namespace PriorAuthBot.Core.Documents;

/// <summary>
/// A document normalized for ingestion (chapter 4): plain text plus the metadata that decides who
/// may see it. Every loader, whether for Markdown, PDF, Word, or a scanned form, produces this type.
/// <see cref="Pages"/> is set for paged formats, so citations can name a page (chapter 8).
/// </summary>
public sealed record SourceDocument(
    string Id,
    string Title,
    string Text,
    DocumentAccess Access,
    string Department,
    string? PolicyNumber,
    string SourcePath,
    IReadOnlyList<DocumentPage>? Pages = null);

/// <summary>
/// One page of a paged document. <see cref="Recognized"/> is true when the text came from OCR
/// rather than a text layer. <see cref="Confidence"/> is the OCR engine's average word confidence,
/// from 0 to 1, when the engine reports one; a vision model does not, so it stays null.
/// </summary>
public sealed record DocumentPage(int Number, string Text, bool Recognized = false, double? Confidence = null);

/// <summary>
/// Who may see a document. Copied from the source system's ACL to every chunk at ingestion, and
/// enforced inside the vector query at retrieval (chapters 6 and 7).
/// </summary>
public sealed record DocumentAccess(IReadOnlyList<string> AllowedGroups, string Sensitivity, bool ContainsPhi)
{
    /// <summary>A document with no allowed groups is visible to nobody: deny by default.</summary>
    public bool IsDenyAll => AllowedGroups.Count == 0;
}
