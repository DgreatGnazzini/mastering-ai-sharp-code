using PriorAuthBot.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Core.Security;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Chapter 12: the prompt shield named in configuration (behind a verdict cache) and the output
    /// guard. <see cref="AI.AiServiceCollectionExtensions.UsePriorAuthBotPipeline"/> puts both on every chat client.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.TryAddSingleton<IPromptShield>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SecurityOptions>>().Value.PromptShield;
            IPromptShield inner = options.Provider switch
            {
                PromptShieldProviders.Heuristic => new HeuristicPromptShield(),
                PromptShieldProviders.ContentSafety => new ContentSafetyPromptShield(
                    new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, sp.AzureCredential(), options),
                PromptShieldProviders.None => new NoPromptShield(),
                _ => throw new InvalidOperationException($"Unknown prompt shield '{options.Provider}'."),
            };
            return new CachingPromptShield(inner, options, sp.GetService<ILogger<CachingPromptShield>>() ?? NullLogger<CachingPromptShield>.Instance);
        });
        services.TryAddSingleton(sp => new OutputGuard(sp.GetRequiredService<IOptions<SecurityOptions>>().Value));
        return services;
    }
}

/// <summary>No shield: every verdict is clean. For measuring what the shield adds, never for production.</summary>
public sealed class NoPromptShield : IPromptShield
{
    public Task<ShieldVerdict> AnalyzeAsync(string? userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken = default) =>
        Task.FromResult(ShieldVerdict.Clean(documents?.Count ?? 0, "none"));
}
