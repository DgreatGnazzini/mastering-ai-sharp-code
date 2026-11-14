using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Retrieval.Reranking;
using PriorAuthBot.Core.Search;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 8 against real models: what keyword search, hybrid search, reranking, and query
/// rewriting each buy, on the chapter 5 catalog queries and on the golden set. Writes
/// hybrid-catalog-ollama.md and hybrid-golden-set-ollama.md. Runs when PRIORAUTHBOT_TEST_OLLAMA is
/// set; the cross-encoder rows also need PRIORAUTHBOT_TEST_RERANKER (scripts/get-reranker.sh).
/// </summary>
public sealed class LiveHybridSearchTests(ITestOutputHelper output)
{
    private const int RerankCandidates = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly GoldenSet Set = GoldenSet.Load();

    [Fact]
    public async Task CatalogWithOllama()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        await using var host = RagTestHost.Create(LiveSettings.OllamaConfiguration());
        var nomic = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var entries = PolicyCatalog.Load(TestPaths.PolicyCatalog);
        var byTitle = entries.ToDictionary(e => e.Title);
        var queries = JsonSerializer.Deserialize<List<LabeledQuery>>(await File.ReadAllTextAsync(TestPaths.CatalogQueries, ct), Json)!;

        // Chapter 5's best configuration: nomic-embed-text with its task prefixes.
        using var catalog = new PolicyCatalog(nomic, new CatalogOptions { QueryPrefix = "search_query: ", DocumentPrefix = "search_document: " });
        await catalog.BuildAsync(entries, ct);

        var methods = new List<(string Name, Func<string, Task<IReadOnlyList<string>>> Search)>
        {
            ("Semantic (chapter 5)", async q => Titles(await catalog.SearchAsync(q, CatalogSearchMode.Semantic, 5, ct))),
            ("Keyword (BM25)", async q => Titles(await catalog.SearchAsync(q, CatalogSearchMode.Keyword, 5, ct))),
            ("Hybrid (RRF, k = 60)", async q => Titles(await catalog.SearchAsync(q, CatalogSearchMode.Hybrid, 5, ct))),
        };

        var rerankers = new List<IReranker>();
        if (LiveSettings.RerankerModel is { } model)
        {
            rerankers.Add(new CrossEncoderReranker(model));
        }

        rerankers.Add(new LlmReranker(host.Get<IChatClient>()));
        foreach (var reranker in rerankers)
        {
            methods.Add(($"Hybrid + {reranker.Name}", async q =>
            {
                var candidates = await catalog.SearchAsync(q, CatalogSearchMode.Hybrid, RerankCandidates, ct);
                var scores = await reranker.ScoreAsync(q, candidates.Select(c => PolicyCatalog.EmbeddingInput(byTitle[c.Title])).ToList(), ct);
                return candidates.Zip(scores).OrderByDescending(x => x.Second).Take(5).Select(x => x.First.Title).ToList();
            }));
        }

        var report = new StringBuilder("### Catalog search, chapter 8: 195 policy summaries, 28 labeled queries\n\n");
        report.Append("| Method | Paraphrase hit@1 | Paraphrase hit@5 | Exact-term hit@1 | Exact-term hit@5 | MRR | Time per query |\n|---|---|---|---|---|---|---|\n");
        var ranks = new Dictionary<string, List<int?>>();
        foreach (var (name, search) in methods)
        {
            var found = new List<int?>();
            var watch = Stopwatch.StartNew();
            foreach (var query in queries)
            {
                var titles = await search(query.Query);
                var position = titles.ToList().IndexOf(query.Expected);
                found.Add(position < 0 ? null : position + 1);
            }

            var perQuery = watch.Elapsed.TotalMilliseconds / queries.Count;
            ranks[name] = found;
            string Rate(string kind, int k)
            {
                var subset = queries.Select((q, i) => (q, r: found[i])).Where(x => x.q.Kind == kind).ToList();
                return $"{subset.Count(x => x.r <= k)}/{subset.Count}";
            }

            var mrr = found.Average(r => r is { } rank ? 1.0 / rank : 0);
            report.Append(CultureInfo.InvariantCulture, $"| {name} | {Rate("paraphrase", 1)} | {Rate("paraphrase", 5)} | {Rate("exact", 1)} | {Rate("exact", 5)} | {mrr:0.00} | {perQuery:0} ms |\n");
            output.WriteLine($"{name}: done in {watch.Elapsed.TotalSeconds:0.0} s");
        }

        report.Append("\nRank of the expected entry (– means not in the top 5):\n\n| Query | Kind | ");
        report.Append(string.Join(" | ", ranks.Keys)).Append(" |\n|---|---|").Append(string.Concat(ranks.Keys.Select(_ => "---|"))).Append('\n');
        for (var i = 0; i < queries.Count; i++)
        {
            report.Append(CultureInfo.InvariantCulture, $"| {queries[i].Query} | {queries[i].Kind} | ");
            report.Append(string.Join(" | ", ranks.Values.Select(r => r[i]?.ToString(CultureInfo.InvariantCulture) ?? "–"))).Append(" |\n");
        }

        foreach (var reranker in rerankers.OfType<IDisposable>())
        {
            reranker.Dispose();
        }

        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "hybrid-catalog-ollama.md"), report.ToString(), ct);
        output.WriteLine(report.ToString());

        // The point of the chapter, as a floor: hybrid finds at least as many exact terms in the top 5 as semantic search.
        int Exact(string method) => ranks[method].Where((r, i) => queries[i].Kind == "exact").Count(r => r <= 5);
        Assert.True(Exact("Hybrid (RRF, k = 60)") >= Exact("Semantic (chapter 5)"), "Hybrid search found fewer exact-term queries than semantic search.");
    }

    [Fact]
    public async Task GoldenSetRetrievalWithOllama()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;

        var configurations = new List<(string Name, Dictionary<string, string?> Settings)>
        {
            ("Vector only (chapter 7)", new() { ["Retrieval:Mode"] = "Vector" }),
            ("Hybrid", new() { ["Retrieval:Mode"] = "Hybrid" }),
        };
        if (LiveSettings.RerankerModel is { } model)
        {
            configurations.Add(("Hybrid + cross-encoder", new() { ["Retrieval:Rerank:Provider"] = "CrossEncoder", ["Retrieval:Rerank:ModelPath"] = model }));
        }

        configurations.Add(("Hybrid + LLM reranker", new() { ["Retrieval:Rerank:Provider"] = "Llm" }));
        configurations.Add(("Hybrid + 3 rewritten queries", new() { ["Retrieval:QueryRewrite:Enabled"] = "true", ["Retrieval:QueryRewrite:Count"] = "3" }));

        var report = new StringBuilder("### Golden set retrieval, chapter 8: 24 questions, top 6 chunks\n\n");
        report.Append("| Configuration | Expected document found | MRR | Exact-term cases found | Declines kept (no results) | Leaks | Time per question |\n|---|---|---|---|---|---|---|\n");
        var scoreNotes = new StringBuilder();
        var leaks = 0;
        var exactIds = new HashSet<string>(["gs21-cpt-code-claims", "gs23-policy-number-hr", "gs24-policy-number-claims"]);

        foreach (var (name, overrides) in configurations)
        {
            var settings = LiveSettings.OllamaConfiguration();
            settings["Retrieval:Rerank:Candidates"] = RerankCandidates.ToString(CultureInfo.InvariantCulture);
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }

            await using var host = RagTestHost.Create(settings);
            await host.IngestSamplesAsync();
            var retriever = host.Get<SecureRetriever>();

            int withExpected = 0, found = 0, exactFound = 0, declines = 0, declinesKept = 0, configurationLeaks = 0;
            double reciprocal = 0;
            var expectedScores = new List<double>();
            var declineScores = new List<double>();
            var watch = Stopwatch.StartNew();
            foreach (var goldenCase in Set.Cases)
            {
                var caller = Set.Caller(goldenCase.User);
                var results = await retriever.SearchAsync(goldenCase.Question, caller, ct);
                configurationLeaks += results.Count(r => !caller.IsInAnyGroup(r.Record.AllowedGroups) || goldenCase.Forbidden.Contains(r.Record.DocumentId));

                if (goldenCase.Expected.Length > 0)
                {
                    withExpected++;
                    var documents = results.Select(r => r.Record.DocumentId).Distinct().ToList();
                    var rank = documents.IndexOf(goldenCase.Expected[0]);
                    if (rank >= 0)
                    {
                        found++;
                        reciprocal += 1.0 / (rank + 1);
                        exactFound += exactIds.Contains(goldenCase.Id) ? 1 : 0;
                        if (results.First(r => r.Record.DocumentId == goldenCase.Expected[0]).RerankScore is { } s)
                        {
                            expectedScores.Add(s);
                        }
                    }
                }

                if (goldenCase.ExpectDecline)
                {
                    declines++;
                    declinesKept += results.Count == 0 ? 1 : 0;
                    declineScores.AddRange(results.Where(r => r.RerankScore is not null).Select(r => r.RerankScore!.Value));
                }
            }

            leaks += configurationLeaks;
            var perQuestion = watch.Elapsed.TotalMilliseconds / Set.Cases.Count;
            report.Append(CultureInfo.InvariantCulture,
                $"| {name} | {found}/{withExpected} | {reciprocal / withExpected:0.00} | {exactFound}/{exactIds.Count} | {declinesKept}/{declines} | {configurationLeaks} | {perQuestion:0} ms |\n");
            if (expectedScores.Count > 0)
            {
                scoreNotes.Append(CultureInfo.InvariantCulture,
                    $"- {name}: lowest reranker score of a found expected document {expectedScores.Min():0.00}; highest score returned for a question that should be declined {(declineScores.Count > 0 ? declineScores.Max().ToString("0.00", CultureInfo.InvariantCulture) : "none")}.\n");
            }

            output.WriteLine($"{name}: done in {watch.Elapsed.TotalSeconds:0.0} s");
        }

        if (scoreNotes.Length > 0)
        {
            report.Append("\nReranker scores, for choosing Retrieval:Rerank:MinScore:\n\n").Append(scoreNotes);
        }

        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "hybrid-golden-set-ollama.md"), report.ToString(), ct);
        output.WriteLine(report.ToString());
        Assert.Equal(0, leaks);
    }

    private static List<string> Titles(IReadOnlyList<CatalogHit> hits) => hits.Select(h => h.Title).ToList();

    private sealed record LabeledQuery(string Query, string Expected, string Kind);
}
