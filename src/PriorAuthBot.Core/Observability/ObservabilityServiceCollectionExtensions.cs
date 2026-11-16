using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Caching;
using PriorAuthBot.Core.Feedback;

namespace PriorAuthBot.Core.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>Chapter 13: the audit log and its store, the answer cache, and feedback.</summary>
    public static IServiceCollection AddPriorAuthBotObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuditOptions>().Bind(configuration.GetSection(AuditOptions.SectionName));
        services.AddOptions<AnswerCacheOptions>().Bind(configuration.GetSection(AnswerCacheOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IAuditStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
            return options.Store switch
            {
                "InMemory" => new InMemoryAuditStore(),
                "Postgres" => new PostgresAuditStore(
                    sp.GetService<NpgsqlDataSource>() ?? NpgsqlDataSource.Create(configuration.GetConnectionString("priorauthbot")
                        ?? throw new InvalidOperationException("Audit:Store is Postgres, but ConnectionStrings:priorauthbot is not set.")),
                    options.CreateSchema),
                _ => throw new InvalidOperationException($"Unknown audit store '{options.Store}'."),
            };
        });
        services.TryAddSingleton<AuditLog>();
        services.TryAddSingleton<SemanticAnswerCache>();
        services.TryAddSingleton<IFeedbackStore, InMemoryFeedbackStore>();
        services.TryAddSingleton<FeedbackService>();
        return services;
    }
}
