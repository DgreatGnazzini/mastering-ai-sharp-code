namespace PriorAuthBot.Core.Documents;

/// <summary>
/// Turns an image of text, or a scanned PDF, into pages of text (chapter 4). Two implementations:
/// Azure AI Document Intelligence, which returns layout and calibrated word confidence, and a vision
/// model through <see cref="Microsoft.Extensions.AI.IChatClient"/>, which returns text only.
/// </summary>
public interface IOcrService
{
    Task<IReadOnlyList<DocumentPage>> RecognizeAsync(BinaryData content, string mediaType, CancellationToken cancellationToken = default);
}
