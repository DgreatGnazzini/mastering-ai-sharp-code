using Azure.AI.DocumentIntelligence;
using PriorAuthBot.Core.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Agents;
using PriorAuthBot.Core.Agents.Exceptions;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Intake;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Mcp;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PriorAuthBot.Core;

public static class PriorAuthBotServiceCollectionExtensions
{
    /// <summary>Registers everything PriorAuthBot needs, as of chapter 7 (and the chapters before it).</summary>
    public static IServiceCollection AddPriorAuthBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPriorAuthBotAI(configuration);
        services.AddPriorAuthBotVectorStore(configuration);

        services.AddSingleton<TokenCounter>();
        services.AddSingleton(sp => new QuestionClassifier(sp.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey)));

        // Chapter 4: loaders for PDF, Word, and scanned documents, OCR, and intake form extraction.
        services.AddOptions<DocumentOptions>().Bind(configuration.GetSection(DocumentOptions.SectionName));
        services.AddSingleton(sp => new DocumentLoader(CreateOcr(sp), sp.GetService<Microsoft.Extensions.Logging.ILogger<DocumentLoader>>()));
        services.AddSingleton(sp => new IntakeFormExtractor(sp.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey)));

        services.AddOptions<ChunkingOptions>().Bind(configuration.GetSection(ChunkingOptions.SectionName));
        services.AddSingleton<IChunker>(sp => new StructuralChunker(
            sp.GetRequiredService<TokenCounter>(), sp.GetRequiredService<IOptions<ChunkingOptions>>().Value));
        services.AddSingleton<IngestionPipeline>();

        services.AddOptions<ContextOptions>().Bind(configuration.GetSection(ContextOptions.SectionName));
        services.AddSingleton<ContextAssembler>();
        services.AddSingleton<RagAnswerService>();

        services.AddOptions<GroupMappingOptions>().Bind(configuration.GetSection(GroupMappingOptions.SectionName));
        services.AddSingleton<CallerIdentityFactory>();

        services.AddPriorAuthBotTools(configuration);
        services.AddPriorAuthBotAgents(configuration);
        services.AddPriorAuthBotObservability(configuration);
        return services;
    }

    /// <summary>
    /// Chapter 11: the policy agent and the policy exception workflow, their limits, and where their
    /// conversations, runs, and checkpoints are kept between requests.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.SectionName));

        // Memory on a laptop. With more than one API instance, register Redis or SQL Server's
        // IDistributedCache instead (chapter 14), and conversations and runs survive a restart.
        services.AddDistributedMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PolicyAgentFactory>();
        services.AddSingleton<AgentConversationStore>();
        services.AddSingleton<PolicyAgentService>();
        services.TryAddSingleton<IExceptionRegister, InMemoryExceptionRegister>();
        services.AddSingleton<ExceptionRunStore>();
        services.AddSingleton<DistributedCheckpointStore>();
        services.AddSingleton<ExceptionRequestService>();
        return services;
    }

    /// <summary>
    /// Chapter 10: PriorAuthBot's tools, the on-behalf-of token provider for the HR API, the pending
    /// approvals, and the allowlisted MCP servers.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HrApiOptions>().Bind(configuration.GetSection(HrApiOptions.SectionName));
        services.AddOptions<DownstreamApiOptions>().Bind(configuration.GetSection(DownstreamApiOptions.SectionName))

            // Chapter 14: the app's one managed identity and tenant, unless the on-behalf-of flow names its own.
            .PostConfigure(options =>
            {
                var azure = AzureIdentity.Options(configuration);
                options.ManagedIdentityClientId ??= azure.ManagedIdentityClientId;
                options.TenantId ??= azure.TenantId;
            });
        services.AddOptions<McpOptions>().Bind(configuration.GetSection(McpOptions.SectionName));

        services.TryAddSingleton<IDownstreamTokenProvider, OnBehalfOfTokenProvider>();
        services.TryAddSingleton(sp => new HrClient(
            new HttpClient { BaseAddress = sp.GetRequiredService<IOptions<HrApiOptions>>().Value.BaseAddress, Timeout = TimeSpan.FromSeconds(10) },
            sp.GetRequiredService<IDownstreamTokenProvider>(),
            sp.GetRequiredService<IOptions<HrApiOptions>>()));
        services.TryAddSingleton<IHelpDesk>(_ => new InMemoryHelpDesk());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new PendingActionStore(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<PriorAuthBotTools>();
        services.AddSingleton(sp => new McpToolProvider(
            sp.GetRequiredService<IOptions<McpOptions>>(),
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        services.AddSingleton<AssistantService>();
        return services;
    }

    /// <summary>The OCR service named in configuration, or null when scanned pages should be skipped.</summary>
    public static IOcrService? CreateOcr(IServiceProvider sp)
    {
        var documents = sp.GetRequiredService<IOptions<DocumentOptions>>().Value;
        var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        return documents.Ocr switch
        {
            "None" => null,
            _ when ai.Provider == "Offline" => null,
            "Vision" => new VisionOcrService(sp.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey)),
            "DocumentIntelligence" => new DocumentIntelligenceOcrService(new DocumentIntelligenceClient(
                documents.DocumentIntelligenceEndpoint ?? throw new InvalidOperationException("Documents:DocumentIntelligenceEndpoint is not set."),
                sp.AzureCredential())),
            _ => throw new InvalidOperationException($"Unknown OCR option '{documents.Ocr}'."),
        };
    }
}
