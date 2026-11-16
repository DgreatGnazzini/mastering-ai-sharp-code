using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.LocalModels;

/// <summary>
/// Chapter 15: a new embedding model means a new index. Vectors from two models are not comparable,
/// so ingestion refuses to mix them, retrieval refuses a chunk from another model, and the change is
/// made by building a second collection next to the first.
/// </summary>
public class EmbeddingModelChangeTests
{
    private static readonly CallerIdentity Nurse = new("nurse", "Nurse", ["AllStaff", "Clinical"]);
    private static readonly CallerIdentity Contractor = new("contractor", "Contractor", ["Contractors"]);

    [Fact]
    public async Task EveryChunkIsStampedWithItsEmbeddingModel()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();

        var chunks = await host.Collection.GetAsync(r => r.ChunkIndex >= 0, 1000, cancellationToken: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal("hashing/hashing-v1@768", c.EmbeddingModel));
    }

    [Fact]
    public async Task IngestionRefusesToMixModelsInOneCollection()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();
        var renamed = Pipeline(host, new RenamedEmbeddingGenerator("hashing-v2"));

        var ex = await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => renamed.SyncAsync(Samples(), TestContext.Current.CancellationToken));

        Assert.Contains("embedded with hashing/hashing-v1@768", ex.Message, StringComparison.Ordinal);
        Assert.Contains("VectorStore:Collection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReembedRebuildsEveryDocumentInPlace()
    {
        await using var host = RagTestHost.Create();
        var first = await host.IngestSamplesAsync();
        var renamed = Pipeline(host, new RenamedEmbeddingGenerator("hashing-v2"), EmbeddingModelChange.Reembed);

        var report = await renamed.SyncAsync(Samples(), TestContext.Current.CancellationToken);
        var chunks = await host.Collection.GetAsync(r => r.ChunkIndex >= 0, 1000, cancellationToken: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first.Added, report.Updated);
        Assert.Equal(0, report.Unchanged);
        Assert.All(chunks, c => Assert.Equal("hashing/hashing-v2@768", c.EmbeddingModel));
    }

    [Fact]
    public async Task RetrievalRefusesAChunkFromAnotherModel()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();
        var retriever = new SecureRetriever(
            host.Collection,
            new RenamedEmbeddingGenerator("hashing-v2"),
            host.Get<IOptions<RetrievalOptions>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SecureRetriever>.Instance,
            host.Get<Core.Retrieval.Keyword.IKeywordSearch>());

        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() =>
            retriever.SearchAsync("When must a positive sepsis screen be escalated to a physician?", Nurse, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANewCollectionIsBuiltBesideTheOldOneAndKeepsEveryPermission()
    {
        // The blue-green rebuild: both collections live in one store. The old revision keeps reading
        // policy_chunks while the new one fills policy_chunks_v2, and the permission checks run on the
        // new one before any user is sent to it.
        var store = new CommunityToolkit.VectorData.InMemory.InMemoryVectorStore();
        await using var current = RagTestHost.Create(configure: s => s.AddSingleton<Microsoft.Extensions.VectorData.VectorStore>(store));
        await current.IngestSamplesAsync();
        await using var next = RagTestHost.Create(
            new Dictionary<string, string?> { ["VectorStore:Collection"] = "policy_chunks_v2" },
            configure: s =>
            {
                s.AddSingleton<Microsoft.Extensions.VectorData.VectorStore>(store);
                s.AddEmbeddingGenerator(new RenamedEmbeddingGenerator("hashing-v2"));
            });
        var built = await next.IngestSamplesAsync();

        Assert.True(built.Added > 0);
        Assert.Equal("policy_chunks_v2", next.Collection.Name);
        var old = await current.Get<SecureRetriever>().SearchAsync("How many hours of PTO can I carry over into next year?", Nurse, TestContext.Current.CancellationToken);
        var rebuilt = await next.Get<SecureRetriever>().SearchAsync("How many hours of PTO can I carry over into next year?", Nurse, TestContext.Current.CancellationToken);
        Assert.Equal(old.Select(r => r.Record.ChunkId), rebuilt.Select(r => r.Record.ChunkId));

        // The ACLs came across with the documents: a contractor still sees nothing clinical.
        var leaked = await next.Get<SecureRetriever>().SearchAsync("When must a positive sepsis screen be escalated to a physician?", Contractor, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(leaked, r => r.Record.DocumentId == "sepsis-screening-protocol");
    }

    [Theory]
    [InlineData("policy_chunks; DROP TABLE x")]
    [InlineData("Policy_Chunks")]
    [InlineData("1chunks")]
    public void ACollectionNameMustBeSafeForSql(string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RagTestHost.Create(new Dictionary<string, string?> { ["VectorStore:Collection"] = name }));

        Assert.Contains("VectorStore:Collection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResponseCacheKeysOnTheModel()
    {
        // MEAI's cache key is the messages and the options. The model a client was built with is in
        // neither, so a cache shared by two models answers the second with the first one's reply.
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var ct = TestContext.Current.CancellationToken;
        using var withoutKey = new DistributedCachingChatClient(new NamedChatClient("old-model"), cache);
        using var stillWithoutKey = new DistributedCachingChatClient(new NamedChatClient("new-model"), cache);
        _ = await withoutKey.GetResponseAsync("How many PTO hours carry over?", cancellationToken: ct);
        Assert.Equal("answer from old-model", (await stillWithoutKey.GetResponseAsync("How many PTO hours carry over?", cancellationToken: ct)).Text);

        var keyed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var oldClient = new NamedChatClient("old-model");
        var newClient = new NamedChatClient("new-model");
        using var oldKeyed = new DistributedCachingChatClient(oldClient, keyed) { CacheKeyAdditionalValues = [ModelIdentity.Of(oldClient)] };
        using var newKeyed = new DistributedCachingChatClient(newClient, keyed) { CacheKeyAdditionalValues = [ModelIdentity.Of(newClient)] };
        _ = await oldKeyed.GetResponseAsync("How many PTO hours carry over?", cancellationToken: ct);
        Assert.Equal("answer from new-model", (await newKeyed.GetResponseAsync("How many PTO hours carry over?", cancellationToken: ct)).Text);
    }

    [Fact]
    public void PriorAuthBotsCachesCarryTheModelInTheirKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPriorAuthBotAI(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Offline",
            ["AI:CacheResponses"] = "true",
            ["AI:CacheEmbeddings"] = "true",
        }).Build());
        using var provider = services.BuildServiceProvider();

        var chatCache = provider.GetRequiredService<IChatClient>().GetService<DistributedCachingChatClient>();
        var embeddingCache = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().GetService<DistributedCachingEmbeddingGenerator<string, Embedding<float>>>();

        Assert.Equal(["offline/offline"], chatCache!.CacheKeyAdditionalValues!.Cast<string>());
        Assert.Equal(["hashing/hashing-v1@768"], embeddingCache!.CacheKeyAdditionalValues!.Cast<string>());
    }

    private static IngestionPipeline Pipeline(RagTestHost host, IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingModelChange onChange = EmbeddingModelChange.Refuse) =>
        new(host.Get<IChunker>(), generator, host.Collection, Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestionPipeline>.Instance,
            storeOptions: Options.Create(new VectorStoreOptions { OnEmbeddingModelChange = onChange }));

    private static IAsyncEnumerable<SourceDocument> Samples() => MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments);

    /// <summary>The offline embedder under another model name: the same vectors, a different identity.</summary>
    private sealed class RenamedEmbeddingGenerator(string model) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly HashingEmbeddingGenerator _inner = new();
        private readonly EmbeddingGeneratorMetadata _metadata = new("hashing", defaultModelId: model, defaultModelDimensions: ChunkDimensions.Size);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            _inner.GenerateAsync(values, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class NamedChatClient(string model) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer from " + model)) { ModelId = model });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("test", null, model) : null;

        public void Dispose()
        {
        }
    }
}
