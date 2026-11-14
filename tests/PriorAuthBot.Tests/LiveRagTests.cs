using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 7's chunking experiment against a real local model. Runs when PRIORAUTHBOT_TEST_OLLAMA
/// is set, as it is in the CI "RAG eval" job. Chapter 7's golden set check, with groundedness and
/// chapter 8's citation faithfulness, moved into the eval project in chapter 9 (eval/PriorAuthBot.Eval).
/// </summary>
public sealed class LiveRagTests(ITestOutputHelper output)
{
    private static readonly GoldenSet Set = GoldenSet.Load();

    /// <summary>
    /// Chunk size and overlap, measured instead of guessed (chapter 7): for each chunker, how often
    /// the expected document is retrieved, and how high it ranks. Writes a table, asserts nothing.
    /// </summary>
    [Fact]
    public async Task ChunkingExperiment()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        var tokens = new TokenCounter();
        (IChunker Chunker, int Overlap)[] chunkers =
        [
            (new FixedSizeChunker(tokens, new ChunkingOptions { MaxTokens = 200, OverlapTokens = 0 }), 0),
            (new FixedSizeChunker(tokens, new ChunkingOptions { MaxTokens = 200, OverlapTokens = 40 }), 40),
            (new StructuralChunker(tokens, new ChunkingOptions { MaxTokens = 100, OverlapTokens = 20 }), 20),
            (new StructuralChunker(tokens, new ChunkingOptions { MaxTokens = 350, OverlapTokens = 50 }), 50),
            (new StructuralChunker(tokens, new ChunkingOptions { MaxTokens = 600, OverlapTokens = 50 }), 50),
        ];

        var documents = await MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, ct).ToListAsync(ct);
        // The chapter 7 and 8 questions only, so the table stays comparable with chapter 7's.
        var cases = Set.Cases.Where(c => c.Expected.Length > 0 && c.Chapter <= 8).ToList();
        var table = new StringBuilder("| Chunker | Overlap | Chunks | Avg tokens | Hit rate @6 | MRR |\n|---|---|---|---|---|---|\n");

        foreach (var (chunker, overlap) in chunkers)
        {
            var settings = LiveSettings.OllamaConfiguration();
            settings["Retrieval:MinScore"] = "-1";
            await using var host = RagTestHost.Create(settings);
            var pipeline = new IngestionPipeline(chunker, host.Get<IEmbeddingGenerator<string, Embedding<float>>>(), host.Collection, Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestionPipeline>.Instance);
            var ingest = await pipeline.SyncAsync(documents.ToAsyncEnumerable(), ct);
            var allChunks = documents.Where(d => !d.Access.IsDenyAll).SelectMany(chunker.Chunk).ToList();

            double hits = 0, reciprocalRanks = 0;
            foreach (var goldenCase in cases)
            {
                var results = await host.Get<SecureRetriever>().SearchAsync(goldenCase.Question, Set.Caller(goldenCase.User), ct);
                var documentRanks = results.Select(r => r.Record.DocumentId).Distinct().ToList();
                var rank = documentRanks.IndexOf(goldenCase.Expected[0]);
                if (rank >= 0)
                {
                    hits++;
                    reciprocalRanks += 1.0 / (rank + 1);
                }
            }

            table.Append(CultureInfo.InvariantCulture,
                $"| {chunker.Name} | {overlap} | {ingest.ChunksWritten} | {allChunks.Average(c => c.TokenCount):0} | {hits / cases.Count:P0} | {reciprocalRanks / cases.Count:0.00} |\n");
        }

        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "chunking-experiment.md"), table.ToString(), ct);
        output.WriteLine(table.ToString());
    }
}
