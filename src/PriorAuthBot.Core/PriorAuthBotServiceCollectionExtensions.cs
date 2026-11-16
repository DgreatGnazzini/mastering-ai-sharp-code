using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Intake;

namespace PriorAuthBot.Core;

public static class PriorAuthBotServiceCollectionExtensions
{
    /// <summary>Registers everything PriorAuthBot needs, as of chapter 4.</summary>
    public static IServiceCollection AddPriorAuthBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPriorAuthBotAI(configuration);

        services.AddSingleton<TokenCounter>();
        services.AddSingleton(sp => new QuestionClassifier(sp.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey)));

        // Chapter 4: loaders for PDF, Word, and scanned documents, OCR, and intake form extraction.
        services.AddOptions<DocumentOptions>().Bind(configuration.GetSection(DocumentOptions.SectionName));
        services.AddSingleton(sp => new DocumentLoader(CreateOcr(sp), sp.GetService<Microsoft.Extensions.Logging.ILogger<DocumentLoader>>()));
        services.AddSingleton(sp => new IntakeFormExtractor(sp.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey)));

        services.AddOptions<GroupMappingOptions>().Bind(configuration.GetSection(GroupMappingOptions.SectionName));
        services.AddSingleton<CallerIdentityFactory>();
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
                new DefaultAzureCredential())),
            _ => throw new InvalidOperationException($"Unknown OCR option '{documents.Ocr}'."),
        };
    }
}
