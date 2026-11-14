using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The golden set against a real local model: nomic-embed-text for retrieval, llama3.2:3b for
/// answers, one Microsoft.Extensions.AI.Evaluation groundedness check, and, from chapter 8,
/// citation faithfulness: is each cited sentence supported by the source it cites? Runs when
/// PRIORAUTHBOT_TEST_OLLAMA is set, as it is in the CI "RAG eval" job.
/// </summary>
public sealed class LiveRagTests(ITestOutputHelper output)
{
    private static readonly GoldenSet Set = GoldenSet.Load();

    /// <summary>Groundedness is scored 1 (unsupported) to 5 (fully supported by the sources).</summary>
    private const double MinAverageGroundedness = 3.5;

    /// <summary>
    /// The share of cited sentences their cited source supports, averaged over the answers (chapter 8).
    /// Set from the first CI run, like the groundedness threshold, and raised only on evidence.
    /// </summary>
    private const double MinAverageCitationFaithfulness = 0.6;

    [Fact]
    public async Task GoldenSetWithOllama()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        await using var host = RagTestHost.Create(LiveSettings.OllamaConfiguration());
        await host.IngestSamplesAsync();

        var retriever = host.Get<SecureRetriever>();
        var assembler = host.Get<ContextAssembler>();
        var rag = host.Get<RagAnswerService>();
        using var judgeClient = new OllamaSharp.OllamaApiClient(
            new HttpClient { BaseAddress = new Uri(LiveSettings.Ollama!), Timeout = TimeSpan.FromMinutes(10) }, LiveSettings.JudgeModel);
        var judge = new ChatConfiguration(judgeClient);
        var evaluator = new GroundednessEvaluator();
        var citationEvaluator = new Evaluation.CitationFaithfulnessEvaluator();
        var faithfulness = new List<double>();
        var coverage = new List<double>();

        var failures = new List<string>();
        var groundedness = new List<double>();
        var containsExpected = 0;
        var answerable = 0;
        var report = new StringBuilder("| Case | User | Top cosine | Declined | Contains expected | Groundedness | Cited | Citation faithfulness |\n|---|---|---|---|---|---|---|---|\n");

        foreach (var goldenCase in Set.Cases)
        {
            var caller = Set.Caller(goldenCase.User);
            var retrieved = await retriever.SearchAsync(goldenCase.Question, caller, ct);
            var context = assembler.Assemble(retrieved);

            foreach (var expected in goldenCase.Expected.Where(e => context.Sources.All(s => s.Record.DocumentId != e)))
            {
                failures.Add($"{goldenCase.Id}: expected {expected} in context.");
            }

            foreach (var leaked in retrieved.Where(r => !caller.IsInAnyGroup(r.Record.AllowedGroups) || goldenCase.Forbidden.Contains(r.Record.DocumentId)))
            {
                failures.Add($"{goldenCase.Id}: {leaked.Record.ChunkId} must not be visible to {goldenCase.User}.");
            }

            var answer = await rag.AskAsync(goldenCase.Question, caller, ct);
            if (goldenCase.ExpectDecline && !answer.Declined)
            {
                failures.Add($"{goldenCase.Id}: expected a decline, got: {answer.Answer}");
            }

            string? score = null;
            string? faithful = null;
            var mustContain = goldenCase.AnswerMustContain ?? [];
            var contains = mustContain.Length > 0 && mustContain.All(p => answer.Answer.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!goldenCase.ExpectDecline && mustContain.Length > 0)
            {
                answerable++;
                containsExpected += contains ? 1 : 0;
                if (answer.Declined)
                {
                    failures.Add($"{goldenCase.Id}: declined a question the sources answer.");
                }
                else
                {
                    // The groundedness check: is every claim in the answer supported by the sources it was given?
                    var messages = RagAnswerService.BuildMessages(goldenCase.Question, context);
                    var result = await evaluator.EvaluateAsync(
                        messages,
                        new ChatResponse(new ChatMessage(ChatRole.Assistant, answer.Answer)),
                        judge,
                        [new GroundednessEvaluatorContext(context.Text)],
                        ct);
                    var metric = result.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName);
                    if (metric.Value is { } value)
                    {
                        groundedness.Add(value);
                        score = value.ToString("0", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        output.WriteLine($"{goldenCase.Id}: judge gave no score. {string.Join(" | ", metric.Diagnostics?.Select(d => d.Message) ?? [])}");
                    }

                    // Chapter 8: is each cited sentence supported by the source it cites?
                    var citations = await citationEvaluator.EvaluateAsync(
                        messages,
                        new ChatResponse(new ChatMessage(ChatRole.Assistant, answer.Answer)),
                        judge,
                        [new Evaluation.CitationSourcesContext(context.Sources.Select(c => c.Record.Text).ToList())],
                        ct);
                    var faithfulMetric = citations.Get<NumericMetric>(Evaluation.CitationFaithfulnessEvaluator.FaithfulnessMetricName);
                    if (faithfulMetric.Value is { } f)
                    {
                        faithfulness.Add(f);
                        faithful = f.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        output.WriteLine($"{goldenCase.Id}: no citation score. {string.Join(" | ", faithfulMetric.Diagnostics?.Select(d => d.Message) ?? [])}");
                    }

                    if (citations.Get<NumericMetric>(Evaluation.CitationFaithfulnessEvaluator.CoverageMetricName).Value is { } c)
                    {
                        coverage.Add(c);
                    }
                }
            }

            var top = retrieved.Max(r => r.VectorScore) is { } best ? best.ToString("0.000", CultureInfo.InvariantCulture) : "-";
            var cited = answer.Declined ? "-" : string.Join(", ", answer.Citations.Select(c => c.Number));
            report.Append(CultureInfo.InvariantCulture, $"| {goldenCase.Id} | {goldenCase.User} | {top} | {answer.Declined} | {(mustContain.Length == 0 ? "-" : contains.ToString())} | {score ?? "-"} | {(cited.Length == 0 ? "none" : cited)} | {faithful ?? "-"} |\n");
            output.WriteLine($"{goldenCase.Id} ({goldenCase.User}): {answer.Answer}");
        }

        var average = groundedness.Count > 0 ? groundedness.Average() : 0;
        var averageFaithfulness = faithfulness.Count > 0 ? faithfulness.Average() : 0;
        report.Append(CultureInfo.InvariantCulture, $"\nJudge: {LiveSettings.JudgeModel}. Average groundedness: {average:0.00} over {groundedness.Count} answers. Answers containing the expected fact: {containsExpected}/{answerable}.\n");
        report.Append(CultureInfo.InvariantCulture, $"Citation faithfulness: {averageFaithfulness:0.00} average over {faithfulness.Count} answers. Citation coverage (sentences that cite a source): {(coverage.Count > 0 ? coverage.Average() : 0):0.00}.\n");
        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "golden-set-ollama.md"), report.ToString(), ct);
        output.WriteLine(report.ToString());

        Assert.True(groundedness.Count >= answerable * 3 / 4, $"Only {groundedness.Count} of {answerable} answers could be scored.");
        Assert.True(average >= MinAverageGroundedness, $"Average groundedness {average:0.00} is below {MinAverageGroundedness}.");
        Assert.True(averageFaithfulness >= MinAverageCitationFaithfulness, $"Average citation faithfulness {averageFaithfulness:0.00} is below {MinAverageCitationFaithfulness}.");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

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
        var cases = Set.Cases.Where(c => c.Expected.Length > 0).ToList();
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
