using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

public class ChunkerTests
{
    private static readonly TokenCounter Tokens = new();

    [Fact]
    public async Task StructuralChunksFollowHeadings()
    {
        var pto = await LoadAsync("pto-policy");
        var chunks = new StructuralChunker(Tokens, new ChunkingOptions()).Chunk(pto);

        Assert.Equal(["Eligibility", "Accrual", "Carryover", "Requesting time off", "Payout at separation"], chunks.Select(c => c.Section));
        Assert.Contains("40 hours", chunks.Single(c => c.Section == "Carryover").Text, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void NestedHeadingsBecomeAPath()
    {
        var document = Document("# Title\n\n## Claims\n\nIntro.\n\n### Appeals\n\nAppeal text.\n\n## Other\n\nOther text.");
        var chunks = new StructuralChunker(Tokens, new ChunkingOptions()).Chunk(document);

        Assert.Equal(["Claims", "Claims > Appeals", "Other"], chunks.Select(c => c.Section));
    }

    [Fact]
    public void LongSectionsAreSplitWithinTheLimit()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i => $"Paragraph {i} explains rule {i} of the claims procedure in some detail, with an example.");
        var document = Document("# Title\n\n## Long section\n\n" + string.Join("\n\n", paragraphs));
        var options = new ChunkingOptions { MaxTokens = 100, OverlapTokens = 30 };

        var chunks = new StructuralChunker(Tokens, options).Chunk(document);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= options.MaxTokens, $"Chunk {c.Index} has {c.TokenCount} tokens."));
        Assert.All(chunks, c => Assert.Equal("Long section", c.Section));

        // Overlap: each chunk after the first starts with the paragraph that ended the previous one.
        for (var i = 1; i < chunks.Count; i++)
        {
            var lastOfPrevious = chunks[i - 1].Text.Split("\n\n")[^1];
            Assert.StartsWith(lastOfPrevious, chunks[i].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FixedSizeChunksIgnoreStructure()
    {
        var pto = await LoadAsync("pto-policy");
        var options = new ChunkingOptions { MaxTokens = 120, OverlapTokens = 20 };
        var chunks = new FixedSizeChunker(Tokens, options).Chunk(pto);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= options.MaxTokens));
        Assert.All(chunks, c => Assert.Empty(c.Section));
    }

    private static SourceDocument Document(string text) =>
        new("test", "Title", text, new DocumentAccess(["AllStaff"], "Internal", false), "AllStaff", null, "test.md");

    private static async Task<SourceDocument> LoadAsync(string id) =>
        MarkdownDocumentLoader.Parse(id, await File.ReadAllTextAsync(Path.Combine(TestPaths.SampleDocuments, id + ".md"), TestContext.Current.CancellationToken), id + ".md");
}
