using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core;

public static class PriorAuthBotServiceCollectionExtensions
{
    /// <summary>Registers everything PriorAuthBot needs, as of chapter 7.</summary>
    public static IServiceCollection AddPriorAuthBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPriorAuthBotAI(configuration);
        services.AddPriorAuthBotVectorStore(configuration);

        services.AddSingleton<TokenCounter>();
        services.AddSingleton(sp => new QuestionClassifier(sp.GetRequiredKeyedService<Microsoft.Extensions.AI.IChatClient>(AiServiceCollectionExtensions.ClassifierKey)));

        services.AddOptions<ChunkingOptions>().Bind(configuration.GetSection(ChunkingOptions.SectionName));
        services.AddSingleton<IChunker>(sp => new StructuralChunker(
            sp.GetRequiredService<TokenCounter>(), sp.GetRequiredService<IOptions<ChunkingOptions>>().Value));
        services.AddSingleton<IngestionPipeline>();

        services.AddOptions<ContextOptions>().Bind(configuration.GetSection(ContextOptions.SectionName));
        services.AddSingleton<ContextAssembler>();
        services.AddSingleton<RagAnswerService>();

        services.AddOptions<GroupMappingOptions>().Bind(configuration.GetSection(GroupMappingOptions.SectionName));
        services.AddSingleton<CallerIdentityFactory>();
        return services;
    }
}
