using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// Builds PriorAuthBot's services the way the API does, from configuration. Defaults to the
/// offline embedder and the in-memory store, so tests need no network, model, or database.
/// </summary>
internal sealed class RagTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private RagTestHost(ServiceProvider services) => _services = services;

    public IServiceProvider Services => _services;

    public static RagTestHost Create(IDictionary<string, string?>? overrides = null, IChatClient? chatClient = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Offline",
            ["VectorStore:Provider"] = "InMemory",
            ["Retrieval:Top"] = "6",
            ["Retrieval:MinScore"] = GoldenSet.Load().Offline.MinScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddPriorAuthBot(configuration);
        if (chatClient is not null)
        {
            services.AddSingleton(chatClient);
        }

        return new RagTestHost(services.BuildServiceProvider());
    }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public VectorStoreCollection<Guid, ChunkRecord> Collection => Get<VectorStoreCollection<Guid, ChunkRecord>>();

    public Task<IngestionReport> IngestSamplesAsync() =>
        Get<IngestionPipeline>().SyncAsync(MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments));

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
