using Azure.Identity;
using Azure.Search.Documents.Indexes;
using CommunityToolkit.VectorData.AzureAISearch;
using CommunityToolkit.VectorData.InMemory;
using CommunityToolkit.VectorData.PgVector;
using CommunityToolkit.VectorData.Qdrant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
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
    /// Registers the vector store named in <c>VectorStore:Provider</c> (chapter 6). Code depends
    /// only on <see cref="VectorStoreCollection{TKey, TRecord}"/>, so the choice is configuration.
    /// What does not port is listed in docs/chapters/ch06.md: key types, collection names, filter
    /// support, and each store's limits.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(VectorStoreOptions.SectionName).Get<VectorStoreOptions>() ?? new VectorStoreOptions();
        var collectionName = CollectionName;
        switch (options.Provider)
        {
            case "InMemory":
                services.AddSingleton<VectorStore>(_ => new InMemoryVectorStore());
                break;
            case "Postgres":
                var connectionString = configuration.GetConnectionString("priorauthbot")
                    ?? throw new InvalidOperationException("ConnectionStrings:priorauthbot is required when VectorStore:Provider is Postgres.");
                services.AddSingleton<VectorStore>(_ => new PostgresVectorStore(connectionString));
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
        services.AddSingleton<SecureRetriever>();
        return services;
    }
}
