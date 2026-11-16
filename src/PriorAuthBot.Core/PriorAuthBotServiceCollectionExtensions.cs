using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;

namespace PriorAuthBot.Core;

public static class PriorAuthBotServiceCollectionExtensions
{
    /// <summary>Registers everything PriorAuthBot needs, as of chapter 3.</summary>
    public static IServiceCollection AddPriorAuthBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPriorAuthBotAI(configuration);

        services.AddSingleton<TokenCounter>();
        services.AddSingleton(sp => new QuestionClassifier(sp.GetRequiredKeyedService<Microsoft.Extensions.AI.IChatClient>(AiServiceCollectionExtensions.ClassifierKey)));
        return services;
    }
}
