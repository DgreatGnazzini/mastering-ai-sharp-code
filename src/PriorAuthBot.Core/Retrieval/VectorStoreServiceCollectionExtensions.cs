using Azure.Identity;
using Azure.Search.Documents.Indexes;
using CommunityToolkit.VectorData.AzureAISearch;
using CommunityToolkit.VectorData.InMemory;
using CommunityToolkit.VectorData.PgVector;
using CommunityToolkit.VectorData.Qdrant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Npgsql;
using PriorAuthBot.Core.Retrieval.Keyword;
using PriorAuthBot.Core.Retrieval.Reranking;
using Qdrant.Client;

namespace PriorAuthBot.Core.Retrieval;

public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    /// <summary><c>InMemory</c>, <c>Postgres</c>, <c>Qdrant</c>, or <c>AzureAISearch</c>.</summary>
    public string Provider { get; set; } = "InMemory";

    public QdrantOptions Qdrant { get; set; } = new();

    public AzureAISearchOptions AzureAISearch { get; set; } = new();
}

public sealed class QdrantOptions
{
    public string Host { get; set; } = "localhost";

    /// <summary>The gRPC port. The REST API and dashboard are on 6333.</summary>
    public int Port { get; set; } = 6334;

    public bool Https { get; set; }

    /// <summary>Set with user secrets or an environment variable. Local Qdrant needs none.</summary>
    public string? ApiKey { get; set; }
}

public sealed class AzureAISearchOptions
{
    /// <summary>The search service, such as <c>https://dd-priorauthbot.search.windows.net</c>. Sign-in is Entra ID.</summary>
    public Uri? Endpoint { get; set; }
}

public static class VectorStoreServiceCollectionExtensions
{
    public const string CollectionName = "policy_chunks";

    /// <summary>Azure AI Search index names allow only lowercase letters, digits, and dashes.</summary>
    public const string AzureAISearchIndexName = "policy-chunks";

    /// <summary>
    /// Turns on pgvector 0.8's iterative index scans for every connection. Without them, an HNSW
    /// search collects <c>hnsw.ef_search</c> candidates (40 by default) and only then applies the
    /// access filter, so a caller who may see 1% of the chunks gets almost nothing back (section 6.8).
    /// </summary>
    public static string WithIterativeScan(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        const string setting = "-c hnsw.iterative_scan=relaxed_order";
        if (builder.Options?.Contains("hnsw.iterative_scan", StringComparison.Ordinal) != true)
        {
            builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? setting : $"{builder.Options} {setting}";
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Registers the vector store named in <c>VectorStore:Provider</c> (chapter 6). Code depends
    /// only on <see cref="VectorStoreCollection{TKey, TRecord}"/>, so the choice is configuration.
    /// What does not port is listed in docs/chapters/ch06.md: key types, collection names, filter
    /// support, and each store's limits.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(VectorStoreOptions.SectionName).Get<VectorStoreOptions>() ?? new VectorStoreOptions();
        var collectionName = CollectionName;
        string? postgres = null;
        switch (options.Provider)
        {
            case "InMemory":
                services.AddSingleton<VectorStore>(_ => new InMemoryVectorStore());
                break;
            case "Postgres":
                var connectionString = configuration.GetConnectionString("priorauthbot")
                    ?? throw new InvalidOperationException("ConnectionStrings:priorauthbot is required when VectorStore:Provider is Postgres.");
                postgres = WithIterativeScan(connectionString);
                services.AddSingleton<VectorStore>(_ => new PostgresVectorStore(postgres));
                break;
            case "Qdrant":
                var qdrant = options.Qdrant;
                services.AddSingleton<VectorStore>(_ => new QdrantVectorStore(
                    new QdrantClient(qdrant.Host, qdrant.Port, qdrant.Https, qdrant.ApiKey), ownsClient: true));
                break;
            case "AzureAISearch":
                var endpoint = options.AzureAISearch.Endpoint
                    ?? throw new InvalidOperationException("VectorStore:AzureAISearch:Endpoint is required when VectorStore:Provider is AzureAISearch.");
                services.AddSingleton<VectorStore>(_ => new AzureAISearchVectorStore(new SearchIndexClient(endpoint, new DefaultAzureCredential())));
                collectionName = AzureAISearchIndexName;
                break;
            default:
                throw new InvalidOperationException($"Unknown vector store provider '{options.Provider}'.");
        }

        services.AddSingleton(sp => sp.GetRequiredService<VectorStore>().GetCollection<Guid, ChunkRecord>(collectionName));
        services.AddOptions<RetrievalOptions>().Bind(configuration.GetSection(RetrievalOptions.SectionName));

        // Chapter 8: the keyword side of hybrid search, one per store. Qdrant and Azure AI Search
        // have their own hybrid search through IKeywordHybridSearchable, which section 8.2 shows;
        // PriorAuthBot runs vector-only on them until the keyword side is ported (docs/chapters/ch08.md).
        switch (options.Provider)
        {
            case "InMemory":
                services.AddSingleton<IKeywordSearch>(sp => new InMemoryKeywordSearch(sp.GetRequiredService<VectorStoreCollection<Guid, ChunkRecord>>()));
                break;
            case "Postgres":
                services.AddSingleton(_ => NpgsqlDataSource.Create(postgres!));
                services.AddSingleton<IKeywordSearch>(sp => new PostgresKeywordSearch(
                    sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<VectorStoreCollection<Guid, ChunkRecord>>(), collectionName));
                break;
            default:
                services.AddSingleton<IKeywordSearch, NoKeywordSearch>();
                break;
        }

        var retrieval = configuration.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>() ?? new RetrievalOptions();
        switch (retrieval.Rerank.Provider)
        {
            case "None":
                break;
            case "CrossEncoder":
                services.AddSingleton<IReranker>(_ => new CrossEncoderReranker(retrieval.Rerank.ModelPath));
                break;
            case "Llm":
                services.AddSingleton<IReranker>(sp => new LlmReranker(sp.GetRequiredService<IChatClient>()));
                break;
            default:
                throw new InvalidOperationException($"Unknown reranker '{retrieval.Rerank.Provider}'. Use None, CrossEncoder, or Llm.");
        }

        services.AddSingleton(sp => new QueryRewriter(sp.GetRequiredService<IChatClient>()));
        services.AddSingleton(sp => new SecureRetriever(
            sp.GetRequiredService<VectorStoreCollection<Guid, ChunkRecord>>(),
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RetrievalOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SecureRetriever>>(),
            sp.GetService<IKeywordSearch>(),
            sp.GetService<IReranker>(),
            sp.GetService<QueryRewriter>()));
        return services;
    }
}
