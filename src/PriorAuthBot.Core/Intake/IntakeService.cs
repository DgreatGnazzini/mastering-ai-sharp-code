using PriorAuthBot.Core.Documents;

namespace PriorAuthBot.Core.Intake;

/// <summary>
/// Runs one intake form from image to routing decision (chapter 4). With Document Intelligence
/// configured, OCR reads the form first and its word confidence takes part in the decision; with a
/// vision model, the model reads and extracts in one step and there is no confidence to check.
/// </summary>
public sealed class IntakeService(IntakeFormExtractor extractor, MemberDirectory members, IOcrService? ocr = null)
{
    public async Task<IntakeResult> ProcessAsync(BinaryData image, string mediaType, CancellationToken cancellationToken = default)
    {
        if (ocr is DocumentIntelligenceOcrService)
        {
            var pages = await ocr.RecognizeAsync(image, mediaType, cancellationToken).ConfigureAwait(false);
            var text = string.Join("\n\n", pages.Select(p => p.Text));
            var confidence = pages.Where(p => p.Confidence is not null).Select(p => p.Confidence!.Value).DefaultIfEmpty(0).Min();
            var form = await extractor.ExtractFromTextAsync(text, cancellationToken).ConfigureAwait(false);
            return IntakeTriage.Decide(form, members, confidence);
        }

        var extracted = await extractor.ExtractFromImageAsync(image, mediaType, cancellationToken).ConfigureAwait(false);
        return IntakeTriage.Decide(extracted, members);
    }
}
