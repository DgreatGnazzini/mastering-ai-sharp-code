using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace PriorAuthBot.Core.AI;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="IChatClient"/> and one <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
    /// for the provider named in configuration. Everything else in PriorAuthBot depends only on the
    /// two interfaces, so swapping Ollama for OpenAI is a configuration change.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotAI(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return options.Provider switch
            {
                "Offline" => new HashingEmbeddingGenerator(),
                "OpenAI" => new OpenAIClient(RequireKey(options))
                    .GetEmbeddingClient(options.OpenAI.EmbeddingModel)
                    .AsIEmbeddingGenerator(ChunkDimensions.Size),
                "Ollama" => new OllamaApiClient(options.Ollama.Endpoint, options.Ollama.EmbeddingModel),
                _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
            };
        });

        services.AddChatClient(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return options.Provider switch
            {
                "Offline" => new OfflineChatClient(),
                "OpenAI" => new OpenAIClient(RequireKey(options)).GetChatClient(options.OpenAI.ChatModel).AsIChatClient(),
                "Ollama" => new OllamaApiClient(options.Ollama.Endpoint, options.Ollama.ChatModel),
                _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
            };
        })
        .UseLogging();

        return services;
    }

    private static string RequireKey(AiOptions options) =>
        options.OpenAI.ApiKey ?? throw new InvalidOperationException(
            "AI:OpenAI:ApiKey is not set. Store it with 'dotnet user-secrets set \"AI:OpenAI:ApiKey\" <key>'.");
}
