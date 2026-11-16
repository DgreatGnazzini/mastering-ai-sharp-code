using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 2: one interface, three providers, chosen by configuration. Nothing here calls a model.</summary>
public class AiServiceCollectionTests
{
    [Theory]
    [InlineData("Ollama", "ollama", "llama3.2:3b")]
    [InlineData("OpenAI", "openai", "gpt-4.1-mini")]
    [InlineData("AzureOpenAI", "openai", "gpt-4.1-mini")]
    public void ProviderIsAConfigurationChoice(string provider, string expectedProvider, string expectedModel)
    {
        using var services = Build(new()
        {
            ["AI:Provider"] = provider,
            ["AI:OpenAI:ApiKey"] = "test-key-not-real",
            ["AI:AzureOpenAI:Endpoint"] = "https://dd-priorauthbot.openai.azure.com/openai/v1/",
        });

        var metadata = services.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>();

        Assert.Equal(expectedProvider, metadata?.ProviderName);
        Assert.Equal(expectedModel, metadata?.DefaultModelId);
    }

    [Fact]
    public void AzureOpenAIUsesTheConfiguredEndpoint()
    {
        using var services = Build(new()
        {
            ["AI:Provider"] = "AzureOpenAI",
            ["AI:AzureOpenAI:Endpoint"] = "https://dd-priorauthbot.openai.azure.com/openai/v1/",
        });

        var metadata = services.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>();

        Assert.Equal("dd-priorauthbot.openai.azure.com", metadata?.ProviderUri?.Host);
    }

    [Fact]
    public void ClassifierCanUseASmallerModel()
    {
        using var services = Build(new() { ["AI:Provider"] = "Ollama", ["AI:Ollama:ClassifierModel"] = "llama3.2:1b" });

        var main = services.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>();
        var classifier = services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey).GetService<ChatClientMetadata>();

        Assert.Equal("llama3.2:3b", main?.DefaultModelId);
        Assert.Equal("llama3.2:1b", classifier?.DefaultModelId);
    }

    [Fact]
    public void EveryClientGetsRetries()
    {
        using var services = Build(new() { ["AI:Provider"] = "Ollama" });

        Assert.NotNull(services.GetRequiredService<IChatClient>().GetService<RetryingChatClient>());
        Assert.NotNull(services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey).GetService<RetryingChatClient>());
    }

    [Fact]
    public void MissingKeyFailsWithInstructions()
    {
        using var services = Build(new() { ["AI:Provider"] = "OpenAI" });

        var ex = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IChatClient>());
        Assert.Contains("user-secrets", ex.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddPriorAuthBotAI(configuration).BuildServiceProvider();
    }
}
