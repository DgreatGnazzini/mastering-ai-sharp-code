using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Search;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 5 against a real embedding model: 28 labeled queries over the policy catalog, with and
/// without nomic-embed-text's task prefixes, next to the offline word-hashing embedder as a keyword
/// baseline. Also times one-at-a-time against batched embedding, and a cached rebuild.
/// Writes catalog-search-ollama.md. Runs when PRIORAUTHBOT_TEST_OLLAMA is set.
/// </summary>
public sealed class LiveSemanticSearchTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task MeasuresTheCatalogWithOllama()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        await using var host = RagTestHost.Create(LiveSettings.OllamaConfiguration());
        var nomic = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var entries = PolicyCatalog.Load(TestPaths.PolicyCatalog);
        var queries = JsonSerializer.Deserialize<List<LabeledQuery>>(await File.ReadAllTextAsync(TestPaths.CatalogQueries, ct), Json)!;
        var report = new StringBuilder();

        // 1. Retrieval quality, three ways.
        var configurations = new (string Name, IEmbeddingGenerator<string, Embedding<float>> Generator, CatalogOptions Options)[]
        {
            ($"{LiveSettings.EmbeddingModel}, no prefixes", nomic, new CatalogOptions()),
            ($"{LiveSettings.EmbeddingModel}, with task prefixes", nomic, new CatalogOptions { QueryPrefix = "search_query: ", DocumentPrefix = "search_document: " }),
            ("Word hashing (keyword baseline, no model)", new HashingEmbeddingGenerator(), new CatalogOptions()),
        };

        report.Append("### Catalog search: 195 policy summaries, 28 labeled queries\n\n");
        report.Append("| Embedder | Paraphrase hit@1 | Paraphrase hit@5 | Exact-term hit@1 | Exact-term hit@5 |\n|---|---|---|---|---|\n");
        var ranks = new Dictionary<string, List<int?>>();
        foreach (var (name, generator, options) in configurations)
        {
            using var catalog = new PolicyCatalog(generator, options);
            await catalog.BuildAsync(entries, ct);
            var found = new List<int?>();
            foreach (var query in queries)
            {
                var hits = await catalog.SearchAsync(query.Query, CatalogSearchMode.Semantic, 5, ct);
                var position = hits.ToList().FindIndex(h => h.Title == query.Expected);
                found.Add(position < 0 ? null : position + 1);
            }

            ranks[name] = found;
            string Rate(string kind, int k)
            {
                var subset = queries.Select((q, i) => (q, r: found[i])).Where(x => x.q.Kind == kind).ToList();
                return $"{subset.Count(x => x.r <= k)}/{subset.Count}";
            }

            report.Append(CultureInfo.InvariantCulture, $"| {name} | {Rate("paraphrase", 1)} | {Rate("paraphrase", 5)} | {Rate("exact", 1)} | {Rate("exact", 5)} |\n");
        }

        report.Append("\nRank of the expected entry (– means not in the top 5):\n\n| Query | Kind | ");
        report.Append(string.Join(" | ", ranks.Keys)).Append(" |\n|---|---|").Append(string.Concat(ranks.Keys.Select(_ => "---|"))).Append('\n');
        for (var i = 0; i < queries.Count; i++)
        {
            report.Append(CultureInfo.InvariantCulture, $"| {queries[i].Query} | {queries[i].Kind} | ");
            report.Append(string.Join(" | ", ranks.Values.Select(r => r[i]?.ToString(CultureInfo.InvariantCulture) ?? "–"))).Append(" |\n");
        }

        // 2. Cost of building the index: one text per request, batches of 32, and a cached rebuild.
        report.Append("\n### Building the index\n\n| How | Requests | Time |\n|---|---|---|\n");
        foreach (var batchSize in new[] { 1, 32 })
        {
            using var catalog = new PolicyCatalog(nomic, new CatalogOptions { BatchSize = batchSize });
            var stats = await catalog.BuildAsync(entries, ct);
            report.Append(CultureInfo.InvariantCulture, $"| Batch size {batchSize} | {stats.Requests} | {stats.Elapsed.TotalSeconds:0.0} s |\n");
        }

        var memory = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        using var cached = new DistributedCachingEmbeddingGenerator<string, Embedding<float>>(nomic, memory);
        using (var warm = new PolicyCatalog(cached, new CatalogOptions()))
        {
            await warm.BuildAsync(entries, ct);
        }

        using (var rebuilt = new PolicyCatalog(cached, new CatalogOptions()))
        {
            var stats = await rebuilt.BuildAsync(entries, ct);
            report.Append(CultureInfo.InvariantCulture, $"| Batch size 32, every text already cached | 0 to the model | {stats.Elapsed.TotalMilliseconds:0} ms |\n");
        }

        // 3. Brute-force search time over the whole index, for scale.
        var vector = await nomic.GenerateVectorAsync("How many vacation days do I get?", cancellationToken: ct);
        var index = new SemanticIndex<int>();
        var all = await nomic.GenerateAsync(entries.Select(PolicyCatalog.EmbeddingInput).ToList(), cancellationToken: ct);
        for (var i = 0; i < all.Count; i++)
        {
            index.Add(i, all[i].Vector);
        }

        var watch = Stopwatch.StartNew();
        const int Repeats = 1000;
        for (var i = 0; i < Repeats; i++)
        {
            index.Search(vector.Span, 5);
        }

        var perSearch = watch.Elapsed.TotalMilliseconds / Repeats;
        report.Append(CultureInfo.InvariantCulture, $"\nBrute-force search over {index.Count} vectors of {vector.Length} dimensions: {perSearch:0.000} ms per query, averaged over {Repeats}. The vectors take {index.Count * vector.Length * sizeof(float) / 1024} KB.\n");

        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "catalog-search-ollama.md"), report.ToString(), ct);
        output.WriteLine(report.ToString());

        // A floor, not a target: the point of the test is the table.
        Assert.True(ranks.First().Value.Where((r, i) => queries[i].Kind == "paraphrase").Count(r => r <= 5) >= 9, "Fewer than half the paraphrase queries found their policy in the top 5.");
    }

    private sealed record LabeledQuery(string Query, string Expected, string Kind);
}
