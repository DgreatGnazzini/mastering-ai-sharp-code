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
    public void VisionUsesItsOwnModel()
    {
        using var services = Build(new() { ["AI:Provider"] = "Ollama" });

        var vision = services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey).GetService<ChatClientMetadata>();

        Assert.Equal("qwen2.5vl:3b", vision?.DefaultModelId);
    }

    [Fact]
    public async Task VisionAsksOllamaForALargerContextWindow()
    {
        // A stand-in for Ollama that records the request body and answers with one finished message.
        using var listener = new System.Net.HttpListener();
        var port = FreePort();
        var prefix = $"http://localhost:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        var body = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var request = await reader.ReadToEndAsync();
            var reply = """{"model":"qwen2.5vl:3b","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true}""" + "\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
            context.Response.ContentType = "application/x-ndjson";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
            return request;
        }, TestContext.Current.CancellationToken);

        using var services = Build(new() { ["AI:Provider"] = "Ollama", ["AI:Ollama:Endpoint"] = prefix });
        var vision = services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey);
        await vision.GetResponseAsync("Read this.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("\"num_ctx\":8192", (await body).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // Ask the operating system for an unused port, then give it back for HttpListener to use.
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Theory]
    [InlineData("Ollama", null, false)]
    [InlineData("OpenAI", null, true)]
    [InlineData("AzureOpenAI", null, true)]
    [InlineData("AzureOpenAI", "true", false)]
    public void ProvidersNotApprovedForPhiGetTheGuard(string provider, string? approved, bool guarded)
    {
        using var services = Build(new()
        {
            ["AI:Provider"] = provider,
            ["AI:ApprovedForPhi"] = approved,
            ["AI:OpenAI:ApiKey"] = "test-key-not-real",
            ["AI:AzureOpenAI:Endpoint"] = "https://dd-priorauthbot.openai.azure.com/openai/v1/",
        });

        foreach (var client in new[]
        {
            services.GetRequiredService<IChatClient>(),
            services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey),
            services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey),
        })
        {
            Assert.Equal(guarded, client.GetService<PhiGuardChatClient>() is not null);
        }
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
