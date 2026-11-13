using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Retrieval;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 6: four stores behind one collection type, chosen by configuration. Nothing here connects.</summary>
public sealed class VectorStoreRegistrationTests
{
    [Theory]
    [InlineData("InMemory", "InMemoryCollection", "policy_chunks")]
    [InlineData("Postgres", "PostgresCollection", "policy_chunks")]
    [InlineData("Qdrant", "QdrantCollection", "policy_chunks")]
    [InlineData("AzureAISearch", "AzureAISearchCollection", "policy-chunks")]
    public void StoreIsAConfigurationChoice(string provider, string expectedType, string expectedName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VectorStore:Provider"] = provider,
            ["ConnectionStrings:priorauthbot"] = "Host=localhost;Database=priorauthbot",
            ["VectorStore:AzureAISearch:Endpoint"] = "https://dd-priorauthbot.search.windows.net",
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddPriorAuthBotVectorStore(configuration).BuildServiceProvider();

        var collection = services.GetRequiredService<VectorStoreCollection<Guid, ChunkRecord>>();

        Assert.StartsWith(expectedType, collection.GetType().Name, StringComparison.Ordinal);
        Assert.Equal(expectedName, collection.Name);
    }

    [Fact]
    public void ChunkKeysAreStableGuids()
    {
        var key = ChunkRecord.MakeKey("pto-policy", 3);

        Assert.Equal(key, ChunkRecord.MakeKey("pto-policy", 3));
        Assert.NotEqual(key, ChunkRecord.MakeKey("pto-policy", 4));
        Assert.Equal(8, key.Version);
        Assert.Equal("pto-policy:0003", ChunkRecord.MakeChunkId("pto-policy", 3));
    }
}
