using CommunityToolkit.VectorData.InMemory;
using CommunityToolkit.VectorData.PgVector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

namespace PriorAuthBot.Core.Retrieval;

public static class VectorStoreServiceCollectionExtensions
{
    public const string CollectionName = "policy_chunks";

    /// <summary>
    /// Registers the vector store named in <c>VectorStore:Provider</c> (chapter 6): <c>InMemory</c>
    /// for tests and a first run, or <c>Postgres</c> for pgvector. Code depends only on
    /// <see cref="VectorStoreCollection{TKey, TRecord}"/>, so the choice is configuration.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["VectorStore:Provider"] ?? "InMemory";
        switch (provider)
        {
            case "InMemory":
                services.AddSingleton<VectorStore>(_ => new InMemoryVectorStore());
                break;
            case "Postgres":
                var connectionString = configuration.GetConnectionString("priorauthbot")
                    ?? throw new InvalidOperationException("ConnectionStrings:priorauthbot is required when VectorStore:Provider is Postgres.");
                services.AddSingleton<VectorStore>(_ => new PostgresVectorStore(connectionString));
                break;
            default:
                throw new InvalidOperationException($"Unknown vector store provider '{provider}'.");
        }

        services.AddSingleton(sp => sp.GetRequiredService<VectorStore>().GetCollection<string, ChunkRecord>(CollectionName));
        services.AddOptions<RetrievalOptions>().Bind(configuration.GetSection(RetrievalOptions.SectionName));
        services.AddSingleton<SecureRetriever>();
        return services;
    }
}
