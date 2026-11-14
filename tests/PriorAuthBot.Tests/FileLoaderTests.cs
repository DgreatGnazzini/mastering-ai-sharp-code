using PriorAuthBot.Core.Documents;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 4: PDF, Word, and scanned files through the document loader.</summary>
public sealed class FileLoaderTests
{
    private static string Sample(string name) => Path.Combine(TestPaths.SampleFiles, name);

    [Fact]
    public async Task TextPdfIsReadPageByPageWithItsSidecarMetadata()
    {
        var document = await new DocumentLoader().LoadAsync(Sample("formulary-exception-process.pdf"), TestContext.Current.CancellationToken);

        Assert.Equal("Formulary Exception Process", document.Title);
        Assert.Equal("PHR-310", document.PolicyNumber);
        Assert.Equal(["Claims", "Clinical"], document.Access.AllowedGroups);
        Assert.NotNull(document.Pages);
        Assert.True(document.Pages.Count >= 2);
        Assert.All(document.Pages, p => Assert.False(p.Recognized));
        Assert.Contains("formulary exception", document.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WordHeadingsAndTablesBecomeMarkdown()
    {
        var document = await new DocumentLoader().LoadAsync(Sample("business-travel-policy.docx"), TestContext.Current.CancellationToken);

        Assert.Equal("FIN-118", document.PolicyNumber);
        Assert.Contains("\n## ", "\n" + document.Text, StringComparison.Ordinal);
        Assert.Contains("|---|", document.Text, StringComparison.Ordinal);
        Assert.Null(document.Pages);
    }

    [Fact]
    public async Task ScannedPdfWithoutOcrFailsLoudly()
    {
        var ex = await Assert.ThrowsAsync<OcrRequiredException>(() =>
            new DocumentLoader().LoadAsync(Sample("fax-handling-procedure-scanned.pdf"), TestContext.Current.CancellationToken));

        Assert.Equal(1, ex.Page);
    }

    [Fact]
    public async Task ScannedPdfPagesGoToOcr()
    {
        var ocr = new FakeOcr("# Fax Handling Procedure\n\nConfirm the fax number before sending.", 0.97);

        var document = await new DocumentLoader(ocr).LoadAsync(Sample("fax-handling-procedure-scanned.pdf"), TestContext.Current.CancellationToken);

        var page = Assert.Single(document.Pages!);
        Assert.True(page.Recognized);
        Assert.Equal(0.97, page.Confidence);
        Assert.Equal("image/png", ocr.MediaTypes.Single());
        Assert.StartsWith("# Fax Handling Procedure", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectorySkipsScansWhenThereIsNoOcr()
    {
        var documents = await new DocumentLoader().LoadDirectoryAsync(TestPaths.SampleFiles, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["business-travel-policy", "formulary-exception-process"], documents.Select(d => d.Id));
    }

    [Fact]
    public async Task MarkdownLoadsExactlyAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, ct).ToListAsync(ct);
        var after = await new DocumentLoader().LoadDirectoryAsync(TestPaths.SampleDocuments, ct).ToListAsync(ct);

        Assert.Equal(before.Select(d => (d.Id, d.Title, d.Text, d.Department)), after.Select(d => (d.Id, d.Title, d.Text, d.Department)));
    }

    [Fact]
    public async Task FileWithoutSidecarIsDenyAll()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var copy = Path.Combine(folder.FullName, "orphan.pdf");
            File.Copy(Sample("formulary-exception-process.pdf"), copy);

            var document = await new DocumentLoader().LoadAsync(copy, TestContext.Current.CancellationToken);

            Assert.True(document.Access.IsDenyAll);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class FakeOcr(string text, double confidence) : IOcrService
    {
        public List<string> MediaTypes { get; } = [];

        public Task<IReadOnlyList<DocumentPage>> RecognizeAsync(BinaryData content, string mediaType, CancellationToken cancellationToken = default)
        {
            MediaTypes.Add(mediaType);
            return Task.FromResult<IReadOnlyList<DocumentPage>>([new DocumentPage(1, text, Recognized: true, Confidence: confidence)]);
        }
    }
}
