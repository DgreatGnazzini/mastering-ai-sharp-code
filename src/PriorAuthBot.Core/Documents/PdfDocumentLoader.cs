using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PriorAuthBot.Core.Documents;

/// <summary>
/// Reads PDFs (chapter 4). A text PDF has a text layer, and PdfPig reads it page by page. A scanned
/// PDF is a picture of a page with no text layer; those pages go to OCR, when an OCR service is
/// configured, and are reported as needing it when one is not.
/// </summary>
public sealed class PdfDocumentLoader(IOcrService? ocr = null)
{
    /// <summary>A page with less text than this, and at least one image, is treated as scanned.</summary>
    public const int ScannedPageThreshold = 20;

    public async Task<IReadOnlyList<DocumentPage>> ReadPagesAsync(string path, CancellationToken cancellationToken = default)
    {
        using var pdf = PdfDocument.Open(path);
        var pages = new List<DocumentPage>();
        foreach (var page in pdf.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length >= ScannedPageThreshold || !page.GetImages().Any())
            {
                pages.Add(new DocumentPage(page.Number, text));
                continue;
            }

            if (ocr is null)
            {
                throw new OcrRequiredException(path, page.Number);
            }

            // A scanned page is usually one full-page image. Send the image itself, not the PDF.
            var image = page.GetImages().OrderByDescending(i => i.BoundingBox.Area).First();
            if (!image.TryGetPng(out var png))
            {
                throw new OcrRequiredException(path, page.Number);
            }

            var recognized = await ocr.RecognizeAsync(BinaryData.FromBytes(png), "image/png", cancellationToken).ConfigureAwait(false);
            pages.Add(new DocumentPage(
                page.Number,
                string.Join("\n\n", recognized.Select(p => p.Text)),
                Recognized: true,
                Confidence: recognized.All(p => p.Confidence is not null) ? recognized.Average(p => p.Confidence!.Value) : null));
        }

        return pages;
    }
}

/// <summary>A scanned page was found and no OCR service is configured.</summary>
public sealed class OcrRequiredException : Exception
{
    public OcrRequiredException()
    {
    }

    public OcrRequiredException(string message)
        : base(message)
    {
    }

    public OcrRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OcrRequiredException(string path, int page)
        : base($"Page {page} of {Path.GetFileName(path)} is a scanned image and needs OCR, but no OCR service is configured.")
    {
        DocumentPath = path;
        Page = page;
    }

    public string? DocumentPath { get; }

    public int Page { get; }
}
