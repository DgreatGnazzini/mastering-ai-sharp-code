using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Search;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 5: cosine similarity, brute-force nearest neighbors, and the policy catalog.</summary>
public sealed class SemanticSearchTests
{
    [Fact]
    public void CosineSimilarityMeasuresDirectionNotLength()
    {
        float[] a = [1, 0], longerA = [5, 0], b = [0, 1], oppositeA = [-1, 0];

        Assert.Equal(1f, TensorPrimitives.CosineSimilarity(a, longerA), 5);
        Assert.Equal(0f, TensorPrimitives.CosineSimilarity(a, b), 5);
        Assert.Equal(-1f, TensorPrimitives.CosineSimilarity(a, oppositeA), 5);
    }

    [Fact]
    public void IndexReturnsTheSameTopKAsSortingEverything()
    {
        var random = new Random(42);
        var index = new SemanticIndex<int>();
        var vectors = Enumerable.Range(0, 500).Select(_ => Enumerable.Range(0, 64).Select(_ => (float)random.NextDouble() - 0.5f).ToArray()).ToList();
        for (var i = 0; i < vectors.Count; i++)
        {
            index.Add(i, vectors[i]);
        }

        var query = vectors[17].Select(v => v + 0.01f).ToArray();
        var expected = vectors.Select((v, i) => (i, Score: TensorPrimitives.CosineSimilarity(query, v))).OrderByDescending(x => x.Score).Take(10).Select(x => x.i);

        var actual = index.Search(query, 10);

        Assert.Equal(expected, actual.Select(r => r.Item));
        Assert.Equal(17, actual[0].Item);
        Assert.True(actual.Zip(actual.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Fact]
    public void VectorsFromDifferentModelsAreRejected()
    {
        var index = new SemanticIndex<string>();
        index.Add("a", new float[768]);

        Assert.Throws<ArgumentException>(() => index.Add("b", new float[1536]));
    }

    [Fact]
    public void CatalogHasAboutTwoHundredUniqueEntries()
    {
        var entries = PolicyCatalog.Load(TestPaths.PolicyCatalog);

        Assert.InRange(entries.Count, 190, 210);
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Summary)));
    }

    [Theory]
    [InlineData(32, 7)]
    [InlineData(100, 2)]
    [InlineData(1, 195)]
    public async Task CatalogEmbedsInBatches(int batchSize, int expectedRequests)
    {
        using var generator = new CountingGenerator();
        using var catalog = new PolicyCatalog(generator, new CatalogOptions { BatchSize = batchSize });

        var stats = await catalog.BuildAsync(PolicyCatalog.Load(TestPaths.PolicyCatalog), TestContext.Current.CancellationToken);

        Assert.Equal(expectedRequests, stats.Requests);
        Assert.Equal(expectedRequests, generator.Requests);
        Assert.Equal(195, generator.Texts);
    }

    [Fact]
    public async Task PrefixesAreAddedToQueriesAndDocuments()
    {
        using var generator = new CountingGenerator();
        using var catalog = new PolicyCatalog(generator, new CatalogOptions { QueryPrefix = "search_query: ", DocumentPrefix = "search_document: " });

        await catalog.BuildAsync(PolicyCatalog.Load(TestPaths.PolicyCatalog).Take(3).ToList(), TestContext.Current.CancellationToken);
        await catalog.SearchAsync("vacation days", cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(generator.Inputs.Take(3), i => Assert.StartsWith("search_document: ", i, StringComparison.Ordinal));
        Assert.Equal("search_query: vacation days", generator.Inputs[^1]);
    }

    [Fact]
    public async Task OfflineSearchFindsWordsItHasSeen()
    {
        using var catalog = new PolicyCatalog(new HashingEmbeddingGenerator(), new CatalogOptions());
        await catalog.BuildAsync(PolicyCatalog.Load(TestPaths.PolicyCatalog), TestContext.Current.CancellationToken);

        var hits = await catalog.SearchAsync("lactation rooms for nursing parents", 3, TestContext.Current.CancellationToken);

        Assert.Equal("Lactation Accommodation", hits[0].Title);
    }

    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly HashingEmbeddingGenerator _inner = new();

        public int Requests { get; private set; }

        public int Texts { get; private set; }

        public List<string> Inputs { get; } = [];

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Requests++;
            Texts += list.Count;
            Inputs.AddRange(list);
            return await _inner.GenerateAsync(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _inner.Dispose();
    }
}
