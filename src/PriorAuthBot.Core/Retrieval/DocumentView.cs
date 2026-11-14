namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// A document as the caller may see it, rebuilt from its chunks (chapter 8). This is where a
/// citation link lands. It is read through the same group filter as search, so it exists only for
/// callers who could have retrieved it.
/// </summary>
public sealed record DocumentView(string DocumentId, string Title, string? PolicyNumber, IReadOnlyList<DocumentViewSection> Sections);

/// <summary>One chunk of the document. <paramref name="Anchor"/> matches the fragment in a citation link.</summary>
public sealed record DocumentViewSection(string Anchor, string Section, int? Page, string Text);
