using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using Xunit;

namespace PriorAuthBot.Tests;

public class ContextAssemblerTests
{
    private static ContextAssembler Assembler(int maxTokens = 1500, int perDocument = 2) =>
        new(new TokenCounter(), Options.Create(new ContextOptions { MaxContextTokens = maxTokens, MaxChunksPerDocument = perDocument }));

    [Fact]
    public void OrdersByScoreAndNumbersSources()
    {
        var context = Assembler().Assemble([Chunk("a", 0, "Alpha text.", 0.6), Chunk("b", 0, "Beta text.", 0.9)]);

        Assert.Equal(["b", "a"], context.Sources.Select(s => s.Record.DocumentId));
        Assert.StartsWith("[1] Doc b", context.Text, StringComparison.Ordinal);
        Assert.Contains("[2] Doc a", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DropsDuplicateTextAndCapsChunksPerDocument()
    {
        var context = Assembler(perDocument: 2).Assemble(
        [
            Chunk("a", 0, "Same   words here.", 0.9),
            Chunk("b", 0, "same words HERE.", 0.8),
            Chunk("a", 1, "Second chunk.", 0.7),
            Chunk("a", 2, "Third chunk.", 0.6),
        ]);

        Assert.Equal(["a:0000", "a:0001"], context.Sources.Select(s => s.Record.Key));
    }

    [Fact]
    public void StaysWithinTheTokenBudget()
    {
        var longText = string.Join(' ', Enumerable.Repeat("coverage", 400));
        var context = Assembler(maxTokens: 250).Assemble([Chunk("a", 0, longText, 0.9), Chunk("b", 0, "Short.", 0.5)]);

        Assert.True(context.TokenCount <= 250);
        Assert.Equal(["b"], context.Sources.Select(s => s.Record.DocumentId));
    }

    private static RetrievedChunk Chunk(string document, int index, string text, double score) => new(
        new ChunkRecord { Key = ChunkRecord.MakeKey(document, index), DocumentId = document, ChunkIndex = index, Title = "Doc " + document, Text = text },
        score);
}
