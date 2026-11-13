using Azure;
using Azure.AI.DocumentIntelligence;

namespace PriorAuthBot.Core.Documents;

/// <summary>
/// OCR with Azure AI Document Intelligence's prebuilt layout model (chapter 4). It returns Markdown
/// with headings and tables kept, page by page, and a confidence for every word, which is what lets
/// the intake pipeline send a low-confidence form to a person instead of guessing.
/// </summary>
public sealed class DocumentIntelligenceOcrService(DocumentIntelligenceClient client) : IOcrService
{
    public async Task<IReadOnlyList<PriorAuthBot.Core.Documents.DocumentPage>> RecognizeAsync(BinaryData content, string mediaType, CancellationToken cancellationToken = default)
    {
        var options = new AnalyzeDocumentOptions("prebuilt-layout", content) { OutputContentFormat = DocumentContentFormat.Markdown };
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, cancellationToken).ConfigureAwait(false);
        var result = operation.Value;

        return result.Pages.Select(page => new PriorAuthBot.Core.Documents.DocumentPage(
            page.PageNumber,
            string.Concat(page.Spans.Select(s => result.Content.Substring(s.Offset, s.Length))).Trim(),
            Recognized: true,
            Confidence: page.Words.Count == 0 ? null : page.Words.Average(w => (double)w.Confidence))).ToList();
    }
}
