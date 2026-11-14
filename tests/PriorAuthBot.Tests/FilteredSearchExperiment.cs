using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.VectorData.PgVector;
using CommunityToolkit.VectorData.Qdrant;
using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Qdrant.Client;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 6's experiment: a selective group filter on an approximate (HNSW) index. 20,000 random
/// vectors, 1% of them visible to the caller's group, top 6 with the filter. Does the store return
/// 6 results, or fewer? Writes filtered-search-{store}.md. Runs where the store is configured.
/// </summary>
public sealed class FilteredSearchExperiment(ITestOutputHelper output)
{
    private const int Records = 20_000;
    private const int VisibleEvery = 100;
    private const int Top = 6;
    private const int Queries = 20;

    [Fact]
    public async Task Pgvector()
    {
        Assert.SkipWhen(LiveSettings.Postgres is null, "Set PRIORAUTHBOT_TEST_POSTGRES to run against pgvector.");
        var report = new StringBuilder("### Filtered search on pgvector (HNSW)\n\n");
        report.Append(Header());

        using (var store = new PostgresVectorStore(LiveSettings.Postgres!))
        {
            var collection = store.GetCollection<Guid, ExperimentRecord>("filter_experiment");
            await LoadAsync(collection);
            report.Append(await MeasureAsync("pgvector, default settings", collection));
        }

        // pgvector 0.8 can keep scanning the index until enough rows pass the filter. PriorAuthBot's
        // registration turns this on for every connection.
        using (var store = new PostgresVectorStore(VectorStoreServiceCollectionExtensions.WithIterativeScan(LiveSettings.Postgres!)))
        {
            var collection = store.GetCollection<Guid, ExperimentRecord>("filter_experiment");
            report.Append(await MeasureAsync("pgvector, hnsw.iterative_scan = relaxed_order", collection));
            await collection.EnsureCollectionDeletedAsync(TestContext.Current.CancellationToken);
        }

        await WriteAsync("filtered-search-pgvector.md", report);
    }

    [Fact]
    public async Task Qdrant()
    {
        Assert.SkipWhen(LiveSettings.Qdrant is null, "Set PRIORAUTHBOT_TEST_QDRANT to run against Qdrant.");
        var parts = LiveSettings.Qdrant!.Split(':');
        using var store = new QdrantVectorStore(new QdrantClient(parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture)), ownsClient: true);
        var collection = store.GetCollection<Guid, ExperimentRecord>("filter_experiment");
        await LoadAsync(collection);

        var report = new StringBuilder("### Filtered search on Qdrant (HNSW)\n\n");
        report.Append(Header()).Append(await MeasureAsync("Qdrant, default settings", collection));
        await collection.EnsureCollectionDeletedAsync(TestContext.Current.CancellationToken);

        await WriteAsync("filtered-search-qdrant.md", report);
    }

    private static string Header() =>
        string.Create(CultureInfo.InvariantCulture, $"{Records:N0} vectors of {ExperimentRecord.Dimensions} dimensions; 1 in {VisibleEvery} visible to the caller's group; top {Top}; {Queries} queries.\n\n| Configuration | Average results returned | Queries with fewer than {Top} | Average time |\n|---|---|---|---|\n");

    private static async Task LoadAsync(VectorStoreCollection<Guid, ExperimentRecord> collection)
    {
        var ct = TestContext.Current.CancellationToken;
        await collection.EnsureCollectionDeletedAsync(ct);
        await collection.EnsureCollectionExistsAsync(ct);
        var random = new Random(7);
        foreach (var batch in Enumerable.Range(0, Records).Chunk(1000))
        {
            await collection.UpsertAsync(batch.Select(i => new ExperimentRecord
            {
                Key = Guid.CreateVersion7(),
                AllowedGroups = [i % VisibleEvery == 0 ? "Clinical" : "AllStaff"],
                Embedding = RandomVector(random),
            }), ct);
        }
    }

    private static async Task<string> MeasureAsync(string name, VectorStoreCollection<Guid, ExperimentRecord> collection)
    {
        var ct = TestContext.Current.CancellationToken;
        var random = new Random(11);
        List<string> groups = ["Clinical"];
        var options = new VectorSearchOptions<ExperimentRecord> { Filter = r => r.AllowedGroups.Any(g => groups.Contains(g)) };
        var counts = new List<int>();
        var watch = new Stopwatch();
        for (var q = 0; q < Queries; q++)
        {
            var vector = RandomVector(random);
            watch.Start();
            var count = 0;
            await foreach (var result in collection.SearchAsync(vector, Top, options, ct))
            {
                Assert.Contains("Clinical", result.Record.AllowedGroups);
                count++;
            }

            watch.Stop();
            counts.Add(count);
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"| {name} | {counts.Average():0.0} | {counts.Count(c => c < Top)} of {Queries} | {watch.Elapsed.TotalMilliseconds / Queries:0.0} ms |\n");
    }

    private static float[] RandomVector(Random random) =>
        Enumerable.Range(0, ExperimentRecord.Dimensions).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();

    private async Task WriteAsync(string file, StringBuilder report)
    {
        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, file), report.ToString(), TestContext.Current.CancellationToken);
        output.WriteLine(report.ToString());
    }

    public sealed class ExperimentRecord
    {
        public const int Dimensions = 64;

        [VectorStoreKey]
        public Guid Key { get; set; }

        [VectorStoreData(IsIndexed = true)]
        public List<string> AllowedGroups { get; set; } = [];

        [VectorStoreVector(Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity, IndexKind = IndexKind.Hnsw)]
        public ReadOnlyMemory<float> Embedding { get; set; }
    }
}
