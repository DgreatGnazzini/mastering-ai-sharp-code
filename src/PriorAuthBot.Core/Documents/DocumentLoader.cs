using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PriorAuthBot.Core.Documents;

/// <summary>
/// The document loader library (chapter 4): Markdown, text PDFs, scanned PDFs, Word files, and
/// images in, a normalized <see cref="SourceDocument"/> out. Ingestion depends only on this class,
/// so a new format is a new case here and nothing else changes.
/// </summary>
public sealed partial class DocumentLoader(IOcrService? ocr = null, ILogger<DocumentLoader>? logger = null)
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
    private readonly ILogger _logger = logger ?? NullLogger<DocumentLoader>.Instance;

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".md" or ".pdf" or ".docx" || ImageTypes.Contains(extension);
    }

    /// <summary>
    /// Loads every supported file in a folder. A file that cannot be read, such as a scanned PDF
    /// with no OCR configured, is logged and skipped; it does not stop the rest.
    /// </summary>
    public async IAsyncEnumerable<SourceDocument> LoadDirectoryAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(directory).Where(IsSupported).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SourceDocument? document = null;
            try
            {
                document = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OcrRequiredException or InvalidDataException or IOException)
            {
                LogSkipped(_logger, Path.GetFileName(path), ex.Message);
            }

            if (document is not null)
            {
                yield return document;
            }
        }
    }

    public async Task<SourceDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        if (extension == ".md")
        {
            var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return MarkdownDocumentLoader.Parse(Path.GetFileNameWithoutExtension(path), raw, path);
        }

        var metadata = await DocumentMetadata.LoadForAsync(path, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DocumentPage>? pages = null;
        string text;
        string? title = metadata.Title;

        if (extension == ".pdf")
        {
            pages = await new PdfDocumentLoader(ocr).ReadPagesAsync(path, cancellationToken).ConfigureAwait(false);
            text = string.Join("\n\n", pages.Select(p => p.Text));
        }
        else if (extension == ".docx")
        {
            text = WordDocumentLoader.ReadText(path);
            title ??= WordDocumentLoader.ReadTitle(path);
        }
        else if (ImageTypes.Contains(extension))
        {
            if (ocr is null)
            {
                throw new OcrRequiredException(path, 1);
            }

            var mediaType = extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "image/tiff",
            };
            pages = await ocr.RecognizeAsync(BinaryData.FromBytes(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)), mediaType, cancellationToken).ConfigureAwait(false);
            text = string.Join("\n\n", pages.Select(p => p.Text));
        }
        else
        {
            throw new InvalidDataException($"Unsupported file type: {extension}");
        }

        var id = Path.GetFileNameWithoutExtension(path);
        return new SourceDocument(
            Id: id,
            Title: title ?? id,
            Text: text.ReplaceLineEndings("\n").Trim(),
            Access: metadata.Access,
            Department: metadata.Department ?? "AllStaff",
            PolicyNumber: metadata.Policy,
            SourcePath: path,
            Pages: pages);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {File}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string file, string reason);
}
