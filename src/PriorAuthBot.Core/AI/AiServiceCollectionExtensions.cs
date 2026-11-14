using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace PriorAuthBot.Core.AI;

public static class AiServiceCollectionExtensions
{
    /// <summary>The keyed chat client for classification: a smaller model when one is configured.</summary>
    public const string ClassifierKey = "classifier";

    /// <summary>The keyed chat client for images: OCR and form extraction (chapter 4).</summary>
    public const string VisionKey = "vision";

    private enum Purpose
    {
        Chat,
        Classifier,
        Vision,
    }

    /// <summary>
    /// Registers PriorAuthBot's <see cref="IChatClient"/>s and its
    /// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for the provider named in configuration.
    /// Everything else in PriorAuthBot depends only on the interfaces, so swapping Ollama for Azure
    /// OpenAI is a configuration change.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotAI(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        if (configuration.GetValue<bool>($"{AiOptions.SectionName}:{nameof(AiOptions.CacheResponses)}"))
        {
            services.AddDistributedMemoryCache();
        }

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return options.Provider switch
            {
                "Offline" => new HashingEmbeddingGenerator(),
                "AzureOpenAI" => CreateAzureOpenAI(options.AzureOpenAI)
                    .GetEmbeddingClient(options.AzureOpenAI.EmbeddingDeployment)
                    .AsIEmbeddingGenerator(ChunkDimensions.Size),
                "OpenAI" => CreateOpenAI(options.OpenAI)
                    .GetEmbeddingClient(options.OpenAI.EmbeddingModel)
                    .AsIEmbeddingGenerator(ChunkDimensions.Size),
                "Ollama" => CreateOllama(options.Ollama, options.Ollama.EmbeddingModel),
                _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
            };
        });

        // The main chat client, plus keyed ones for classification and images. All get the same pipeline.
        services.AddChatClient(sp => CreateChatClient(sp, Purpose.Chat))
            .UsePriorAuthBotPipeline();
        services.AddKeyedChatClient(ClassifierKey, sp => CreateChatClient(sp, Purpose.Classifier))
            .UsePriorAuthBotPipeline();
        services.AddKeyedChatClient(VisionKey, sp => CreateChatClient(sp, Purpose.Vision))
            .UsePriorAuthBotPipeline();

        return services;
    }

    /// <summary>
    /// The middleware every PriorAuthBot chat client gets, outermost first: the PHI guard (only for a
    /// provider not approved for PHI) runs before anything can log or cache the request, logging sees
    /// each call once, the cache answers repeats without a model call, and retries sit closest to the provider.
    /// </summary>
    public static ChatClientBuilder UsePriorAuthBotPipeline(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Use((inner, sp) => sp.GetRequiredService<IOptions<AiOptions>>().Value.IsApprovedForPhi
                ? inner
                : new PhiGuardChatClient(inner))
            .UseLogging()
            .Use((inner, sp) => sp.GetRequiredService<IOptions<AiOptions>>().Value.CacheResponses
                ? new DistributedCachingChatClient(inner, sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>())
                : inner)
            .Use((inner, sp) => new RetryingChatClient(
                inner,
                sp.GetRequiredService<IOptions<AiOptions>>().Value.Resilience,
                sp.GetService<ILoggerFactory>()?.CreateLogger<RetryingChatClient>()));
    }

    private static IChatClient CreateChatClient(IServiceProvider sp, Purpose purpose)
    {
        var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        return options.Provider switch
        {
            "Offline" => new OfflineChatClient(),
            "AzureOpenAI" => CreateAzureOpenAI(options.AzureOpenAI)
                .GetChatClient(Pick(purpose, options.AzureOpenAI.ClassifierDeployment, options.AzureOpenAI.VisionDeployment, options.AzureOpenAI.ChatDeployment))
                .AsIChatClient(),
            "OpenAI" => CreateOpenAI(options.OpenAI)
                .GetChatClient(Pick(purpose, options.OpenAI.ClassifierModel, options.OpenAI.VisionModel, options.OpenAI.ChatModel))
                .AsIChatClient(),
            "Ollama" when purpose == Purpose.Vision => WithContextLength(
                CreateOllama(options.Ollama, Pick(purpose, options.Ollama.ClassifierModel, options.Ollama.VisionModel, options.Ollama.ChatModel)),
                options.Ollama.VisionContextLength),
            "Ollama" => CreateOllama(options.Ollama, Pick(purpose, options.Ollama.ClassifierModel, options.Ollama.VisionModel, options.Ollama.ChatModel)),
            _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
        };
    }

    private static string Pick(Purpose purpose, string? classifierModel, string? visionModel, string chatModel)
    {
        var specific = purpose switch
        {
            Purpose.Classifier => classifierModel,
            Purpose.Vision => visionModel,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(specific) ? chatModel : specific;
    }

    /// <summary>
    /// Azure OpenAI through its OpenAI-compatible v1 endpoint, signed in with Entra ID: no API key
    /// exists to leak. Locally, DefaultAzureCredential uses your Azure CLI or Visual Studio sign-in;
    /// in Azure, the app's managed identity (chapter 14).
    /// </summary>
    private static OpenAIClient CreateAzureOpenAI(AzureOpenAIOptions options)
    {
        var endpoint = options.Endpoint ?? throw new InvalidOperationException(
            "AI:AzureOpenAI:Endpoint is not set. Use the resource's v1 endpoint, such as https://<resource>.openai.azure.com/openai/v1/.");
        var policy = new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default");

        // The token-policy constructor is still marked experimental in OpenAI 2.x (OPENAI001). It is
        // the documented way to call Azure OpenAI's v1 endpoint with Entra ID; chapter 15 revisits it.
#pragma warning disable OPENAI001
        return new OpenAIClient(policy, WithoutSdkRetries(new OpenAIClientOptions { Endpoint = endpoint }));
#pragma warning restore OPENAI001
    }

    private static OpenAIClient CreateOpenAI(OpenAIOptions options) =>
        new(new ApiKeyCredential(options.ApiKey ?? throw new InvalidOperationException(
            "AI:OpenAI:ApiKey is not set. Store it with 'dotnet user-secrets set \"AI:OpenAI:ApiKey\" <key>'.")),
            WithoutSdkRetries(new OpenAIClientOptions()));

    /// <summary>
    /// The OpenAI SDK retries 429s and 5xx on its own. RetryingChatClient already does, for every
    /// provider, so the SDK's retries are turned off: two retry layers multiply into a storm.
    /// </summary>
    private static OpenAIClientOptions WithoutSdkRetries(OpenAIClientOptions options)
    {
        options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        return options;
    }

    /// <summary>
    /// Ollama reads its context window from the request's options as <c>num_ctx</c>. Images are
    /// expensive in tokens, so the vision client asks for a larger window than Ollama's default.
    /// </summary>
    private static IChatClient WithContextLength(IChatClient client, int contextLength) =>
        client.AsBuilder()
            .ConfigureOptions(o => (o.AdditionalProperties ??= [])["num_ctx"] = contextLength)
            .Build();

    private static OllamaApiClient CreateOllama(OllamaOptions options, string model) =>
        new(new HttpClient { BaseAddress = options.Endpoint, Timeout = options.Timeout }, model);
}
