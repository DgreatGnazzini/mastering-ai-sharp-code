using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;

namespace PriorAuthBot.Core;

public static class PriorAuthBotServiceCollectionExtensions
{
    /// <summary>Registers everything PriorAuthBot needs, as of chapter 2.</summary>
    public static IServiceCollection AddPriorAuthBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPriorAuthBotAI(configuration);

        services.AddSingleton<TokenCounter>();
        return services;
    }
}
