namespace PriorAuthBot.Core.Rag;

/// <summary>PriorAuthBot's answer to one question.</summary>
/// <param name="Answer">The answer text, with [n] markers that refer to <paramref name="Sources"/>.</param>
/// <param name="Declined">True when PriorAuthBot said it does not know.</param>
/// <param name="Sources">The documents the answer was built from. Always ones the caller can see.</param>
public sealed record RagAnswer(string Answer, bool Declined, IReadOnlyList<SourceReference> Sources);

/// <summary>A source shown with an answer. Chapter 8 adds pages and links.</summary>
public sealed record SourceReference(int Number, string DocumentId, string Title, string? PolicyNumber, string Section);
