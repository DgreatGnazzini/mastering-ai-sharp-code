using System.ClientModel;
using System.ClientModel.Primitives;
using PriorAuthBot.Core.Data;
using PriorAuthBot.Core.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Security;
using PriorAuthBot.Core.Usage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PriorAuthBot.Core.LocalModels;

namespace PriorAuthBot.Core.AI;

public static class AiServiceCollectionExtensions
{
    /// <summary>The keyed chat client for classification: a smaller model when one is configured.</summary>
    public const string ClassifierKey = "classifier";

    /// <summary>The keyed chat client for images: OCR and form extraction (chapter 4).</summary>
    public const string VisionKey = "vision";

    /// <summary>The keyed chat client that can call tools (chapter 10): the main model, with function invocation.</summary>
    public const string ToolsKey = "tools";

    /// <summary>Chapter 15: the keyed branches behind a routing client, such as <c>route:local:chat</c>.</summary>
    public const string HostedRoutePrefix = "route:hosted:";

    public const string LocalRoutePrefix = "route:local:";

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
        services.AddPriorAuthBotAzureIdentity(configuration);
        services.AddPriorAuthBotSecurity(configuration);
        services.AddPriorAuthBotUsage(configuration);
        if (configuration.GetValue<bool>($"{AiOptions.SectionName}:{nameof(AiOptions.CacheResponses)}")
            || configuration.GetValue<bool>($"{AiOptions.SectionName}:{nameof(AiOptions.CacheEmbeddings)}"))
        {
            services.AddDistributedMemoryCache();
        }

        services.AddEmbeddingGenerator(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return options.Provider switch
            {
                "Offline" => new HashingEmbeddingGenerator(),
                "AzureOpenAI" => CreateAzureOpenAI(options.AzureOpenAI, sp.AzureCredential())
                    .GetEmbeddingClient(options.AzureOpenAI.EmbeddingDeployment)
                    .AsIEmbeddingGenerator(ChunkDimensions.Size),
                "OpenAI" => CreateOpenAI(options.OpenAI)
                    .GetEmbeddingClient(options.OpenAI.EmbeddingModel)
                    .AsIEmbeddingGenerator(ChunkDimensions.Size),
                // Chapter 15: ONNX Runtime GenAI generates text only, so the embeddings stay local on Ollama.
                "Ollama" or "OnnxGenAI" => CreateOllama(options.Ollama, options.Ollama.EmbeddingModel),
                _ => throw new InvalidOperationException($"Unknown AI provider '{options.Provider}'."),
            };
        })
        .UsePriorAuthBotEmbeddingPipeline();

        // Chapter 15: one in-process model, loaded once (after its files are checked) and shared by
        // every client that uses it. Only built if a client asks for it.
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>().Value.OnnxGenAI;
            var lockPath = ModelLock.Find(options.LockFile);
            var locked = lockPath is null ? null : ModelLock.Load(lockPath).Get(options.ModelId);
            return new OnnxGenAIChatClient(options, locked);
        });

        // The main chat client, plus keyed ones for classification, images, and tools. Chapter 10's
        // tool client is the same model with FunctionInvokingChatClient outermost, so every round trip
        // of the tool loop passes through the PHI guard, logging, cache, and retries below it.
        // Chapter 15: with routing on, each of them is a router in front of two complete pipelines.
        var routing = configuration.GetValue<bool>($"{AiOptions.SectionName}:{nameof(AiOptions.Routing)}:{nameof(RoutingOptions.Enabled)}");
        AddChatClients(services, key: null, Purpose.Chat, routing, tools: false);
        AddChatClients(services, ClassifierKey, Purpose.Classifier, routing, tools: false);
        AddChatClients(services, ToolsKey, Purpose.Chat, routing, tools: true);
        if (routing)
        {
            // Images are treated as PHI (a scan cannot be checked with a pattern), so they always go local.
            services.AddKeyedChatClient(VisionKey, sp => CreateChatClient(sp, Purpose.Vision, LocalProvider(sp)))
                .UsePriorAuthBotPipeline(approvedForPhi: true);
        }
        else
        {
            services.AddKeyedChatClient(VisionKey, sp => CreateChatClient(sp, Purpose.Vision))
                .UsePriorAuthBotPipeline();
        }

        return services;
    }

    /// <summary>
    /// Chapter 13: telemetry settings, the per-user token budgets, and the ledger they are counted in.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotUsage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelemetryOptions>().Bind(configuration.GetSection(TelemetryOptions.SectionName));
        services.AddOptions<UsageOptions>().Bind(configuration.GetSection(UsageOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenCounter>();
        // Chapter 14: with more than one replica, the ledger has to be shared, or every user gets one budget per replica.
        if (configuration[$"{UsageOptions.SectionName}:{nameof(UsageOptions.Ledger)}"] == UsageOptions.PostgresLedger)
        {
            services.AddPriorAuthBotPostgres(configuration);
            services.TryAddSingleton<ITokenLedger>(sp => new PostgresTokenLedger(
                sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), sp.GetRequiredService<IOptionsMonitor<UsageOptions>>(), sp.GetRequiredService<TimeProvider>()));
        }

        services.TryAddSingleton<ITokenLedger>(sp => new InMemoryTokenLedger(
            sp.GetRequiredService<IOptionsMonitor<UsageOptions>>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// The middleware every PriorAuthBot chat client gets, outermost first: the PHI guard (only for a
    /// provider not approved for PHI) runs before anything can log, cache, or shield the request; the
    /// guardrails (chapter 12) check what goes in and what comes out; OpenTelemetry (chapter 13) makes
    /// one <c>gen_ai</c> span per call, with message content off unless configured; metering checks
    /// the user's budget and charges what the call used; logging sees each call once, the cache
    /// answers repeats without a model call, and retries sit closest to the provider.
    /// </summary>
    /// <remarks>
    /// OpenTelemetry is inside the PHI guard, so even a developer who turns content capture on sees
    /// redacted text for a provider that is not approved for PHI. Metering is above the response
    /// cache: with <see cref="AiOptions.CacheResponses"/> on, a cached reply is charged as if the model
    /// had produced it, which over-counts cost and errs on the safe side for a budget.
    /// </remarks>
    /// <param name="builder">The client's builder.</param>
    /// <param name="approvedForPhi">
    /// Chapter 15: whether this pipeline's provider may receive PHI. Null reads it from
    /// <see cref="AiOptions.IsApprovedForPhi"/>; the local branch of a routing client passes true.
    /// </param>
    public static ChatClientBuilder UsePriorAuthBotPipeline(this ChatClientBuilder builder, bool? approvedForPhi = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Use((inner, sp) => approvedForPhi ?? sp.GetRequiredService<IOptions<AiOptions>>().Value.IsApprovedForPhi
                ? inner
                : new PhiGuardChatClient(inner))
            .UsePriorAuthBotGuardrails()
            .Use((inner, sp) => new OpenTelemetryChatClient(inner, Logger<OpenTelemetryChatClient>(sp), PriorAuthBotTelemetry.AiSourceName)
            {
                // Set explicitly from PriorAuthBot's own setting, so no environment variable can turn it on.
                EnableSensitiveData = sp.GetService<IOptions<TelemetryOptions>>()?.Value.CaptureMessageContent ?? false,
            })
            .Use((inner, sp) => new MeteringChatClient(
                inner,
                sp.GetRequiredService<ITokenLedger>(),
                sp.GetRequiredService<IOptionsMonitor<UsageOptions>>(),
                sp.GetRequiredService<TokenCounter>(),
                Logger<MeteringChatClient>(sp)))
            .UseLogging()
            // Chapter 15: the model is part of the cache key. MEAI's key is the messages and the options,
            // and the model a client was built with is in neither, so without this a new model is
            // answered with the old model's cached replies, and its eval measures the old model.
            .Use((inner, sp) => sp.GetRequiredService<IOptions<AiOptions>>().Value.CacheResponses
                ? new DistributedCachingChatClient(inner, sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>())
                {
                    CacheKeyAdditionalValues = [ModelIdentity.Of(inner)],
                }
                : inner)
            .Use((inner, sp) => new RetryingChatClient(
                inner,
                sp.GetRequiredService<IOptions<AiOptions>>().Value.Resilience,
                sp.GetService<ILoggerFactory>()?.CreateLogger<RetryingChatClient>()));
    }

    /// <summary>
    /// Chapter 12: the prompt shield, then the output guard. The shield is outer, so the output guard
    /// compares the answer with the request the model actually got, after any flagged source was removed.
    /// <see cref="SecurityOptions.Guardrails"/> turns both off, for the red-team eval's comparison run.
    /// </summary>
    public static ChatClientBuilder UsePriorAuthBotGuardrails(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Use((inner, sp) => sp.GetRequiredService<IOptions<SecurityOptions>>().Value is { Guardrails: true } security
                ? new PromptShieldChatClient(inner, sp.GetRequiredService<IPromptShield>(), security, Logger<PromptShieldChatClient>(sp))
                : inner)
            .Use((inner, sp) => sp.GetRequiredService<IOptions<SecurityOptions>>().Value.Guardrails
                ? new OutputGuardChatClient(inner, sp.GetRequiredService<OutputGuard>(), Logger<OutputGuardChatClient>(sp))
                : inner);
    }

    /// <summary>
    /// Registers one chat client: the configured provider with the full pipeline or, with routing on,
    /// a <see cref="PhiRoutingChatClient"/> in front of a hosted branch and a local branch, each with
    /// the full pipeline. The local branch is approved for PHI, so it has no PHI guard; the hosted branch
    /// keeps the guard unless its provider is approved.
    /// </summary>
    private static void AddChatClients(IServiceCollection services, string? key, Purpose purpose, bool routing, bool tools)
    {
        if (!routing)
        {
            var builder = key is null
                ? services.AddChatClient(sp => CreateChatClient(sp, purpose))
                : services.AddKeyedChatClient(key, sp => CreateChatClient(sp, purpose));
            (tools ? builder.UsePriorAuthBotToolInvocation() : builder).UsePriorAuthBotPipeline();
            return;
        }

        var name = key ?? "chat";
        var hostedKey = HostedRoutePrefix + name;
        var localKey = LocalRoutePrefix + name;
        var hosted = services.AddKeyedChatClient(hostedKey, sp => CreateChatClient(sp, purpose));
        (tools ? hosted.UsePriorAuthBotToolInvocation() : hosted).UsePriorAuthBotPipeline();
        var local = services.AddKeyedChatClient(localKey, sp => CreateChatClient(sp, purpose, LocalProvider(sp)));
        (tools ? local.UsePriorAuthBotToolInvocation() : local).UsePriorAuthBotPipeline(approvedForPhi: true);

        IChatClient Router(IServiceProvider sp) => new PhiRoutingChatClient(
            sp.GetRequiredKeyedService<IChatClient>(hostedKey), sp.GetRequiredKeyedService<IChatClient>(localKey), Logger<PhiRoutingChatClient>(sp));
        if (key is null)
        {
            services.AddChatClient(Router);
        }
        else
        {
            services.AddKeyedChatClient(key, Router);
        }
    }

    private static string LocalProvider(IServiceProvider sp)
    {
        var local = sp.GetRequiredService<IOptions<AiOptions>>().Value.Routing.LocalProvider;
        // Offline counts as local, so the routing tests run with no model at all.
        return AiOptions.IsLocalProvider(local)
            ? local
            : throw new InvalidOperationException($"AI:Routing:LocalProvider is '{local}', which is not a local provider. Use Ollama or OnnxGenAI.");
    }

    private static ILogger<T> Logger<T>(IServiceProvider sp) =>
        sp.GetService<ILoggerFactory>()?.CreateLogger<T>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    /// <summary>
    /// Function invocation with PriorAuthBot's limits (chapter 10). The loop stops after a few round
    /// trips, so a model that keeps calling tools cannot run up cost; one call runs at a time; and an
    /// exception's message never goes back to the model, which would put internal details in its context.
    /// </summary>
    public static ChatClientBuilder UsePriorAuthBotToolInvocation(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseFunctionInvocation(configure: client =>
        {
            client.MaximumIterationsPerRequest = 4;
            client.MaximumConsecutiveErrorsPerRequest = 2;
            client.AllowConcurrentInvocation = false;
            client.IncludeDetailedErrors = false;
        });
    }

    /// <summary>
    /// The middleware every embedding generator gets (chapter 5), in the same order as the chat
    /// pipeline: the PHI guard first, then OpenTelemetry (chapter 13), logging, and an optional cache. Embeddings are a good
    /// fit for caching, because the same text always gets the same vector from the same model.
    /// </summary>
    public static EmbeddingGeneratorBuilder<string, Embedding<float>> UsePriorAuthBotEmbeddingPipeline(this EmbeddingGeneratorBuilder<string, Embedding<float>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Use((inner, sp) => sp.GetRequiredService<IOptions<AiOptions>>().Value.IsApprovedForPhi
                ? inner
                : new PhiGuardEmbeddingGenerator(inner))
            .Use((inner, sp) => new OpenTelemetryEmbeddingGenerator<string, Embedding<float>>(inner, Logger<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>(sp), PriorAuthBotTelemetry.AiSourceName)
            {
                EnableSensitiveData = sp.GetService<IOptions<TelemetryOptions>>()?.Value.CaptureMessageContent ?? false,
            })
            .UseLogging()
            .Use((inner, sp) => sp.GetRequiredService<IOptions<AiOptions>>().Value.CacheEmbeddings
                ? new DistributedCachingEmbeddingGenerator<string, Embedding<float>>(inner, sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>())
                {
                    // Chapter 15: a vector from one model is meaningless to another, so the model is in the key.
                    CacheKeyAdditionalValues = [ModelIdentity.Of(inner)],
                }
                : inner);
    }

    private static IChatClient CreateChatClient(IServiceProvider sp, Purpose purpose, string? provider = null)
    {
        var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        return (provider ?? options.Provider) switch
        {
            "Offline" => new OfflineChatClient(),
            // Chapter 15: ONNX Runtime GenAI reads text only; images go to the local vision model on Ollama.
            "OnnxGenAI" when purpose == Purpose.Vision => WithContextLength(
                CreateOllama(options.Ollama, options.Ollama.VisionModel), options.Ollama.VisionContextLength),
            "OnnxGenAI" => sp.GetRequiredService<OnnxGenAIChatClient>(),
            "AzureOpenAI" => CreateAzureOpenAI(options.AzureOpenAI, sp.AzureCredential())
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
    /// in Azure, the app's managed identity (chapter 14). The credential is the one
    /// <see cref="AzureIdentity.AddPriorAuthBotAzureIdentity"/> registered.
    /// </summary>
    public static OpenAIClient CreateAzureOpenAI(AzureOpenAIOptions options, Azure.Core.TokenCredential credential)
    {
        var endpoint = options.Endpoint ?? throw new InvalidOperationException(
            "AI:AzureOpenAI:Endpoint is not set. Use the resource's v1 endpoint, such as https://<resource>.openai.azure.com/openai/v1/.");
        var policy = new BearerTokenPolicy(credential, AzureIdentity.CognitiveServicesScope);

        // The token-policy constructor is still marked experimental in OpenAI 2.x (OPENAI001). It is
        // the documented way to call Azure OpenAI's v1 endpoint with Entra ID. Chapter 15 lists it in
        // docs/experimental-apis.md, with when to look again.
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
