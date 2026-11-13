namespace PriorAuthBot.Core.Documents;

/// <summary>
/// A document normalized for ingestion (chapter 4): plain text plus the metadata that decides who
/// may see it. Every loader, whether for Markdown, PDF, Word, or a scanned form, produces this type.
/// </summary>
public sealed record SourceDocument(
    string Id,
    string Title,
    string Text,
    DocumentAccess Access,
    string Department,
    string? PolicyNumber,
    string SourcePath);

/// <summary>
/// Who may see a document. Copied from the source system's ACL to every chunk at ingestion, and
/// enforced inside the vector query at retrieval (chapters 6 and 7).
/// </summary>
public sealed record DocumentAccess(IReadOnlyList<string> AllowedGroups, string Sensitivity, bool ContainsPhi)
{
    /// <summary>A document with no allowed groups is visible to nobody: deny by default.</summary>
    public bool IsDenyAll => AllowedGroups.Count == 0;
}
