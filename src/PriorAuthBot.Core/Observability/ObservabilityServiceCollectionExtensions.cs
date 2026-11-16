using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Data;
using PriorAuthBot.Core.Hosting;
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

        if (configuration[$"{AuditOptions.SectionName}:Store"] == "Postgres")
        {
            services.AddPriorAuthBotPostgres(configuration);
        }

        services.TryAddSingleton<IAuditStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
            return options.Store switch
            {
                "InMemory" => new InMemoryAuditStore(),
                "Postgres" => new PostgresAuditStore(sp.GetRequiredService<NpgsqlDataSource>(), options.CreateSchema),
                _ => throw new InvalidOperationException($"Unknown audit store '{options.Store}'."),
            };
        });
        services.TryAddSingleton<AuditLog>();

        // Chapter 14: the second copy and the chain's anchors, in immutable Blob Storage.
        var archive = configuration.GetSection($"{AuditOptions.SectionName}:Archive").Get<AuditArchiveOptions>() ?? new AuditArchiveOptions();
        if (archive.Provider == AuditArchiveOptions.Blob)
        {
            var container = archive.ContainerUri ?? throw new InvalidOperationException("Audit:Archive:ContainerUri is required when Audit:Archive:Provider is Blob.");
            services.TryAddSingleton<IAuditArchive>(sp => new BlobAuditArchive(new Azure.Storage.Blobs.BlobContainerClient(container, sp.AzureCredential())));
            services.AddHostedService(sp => ActivatorUtilities.CreateInstance<AuditArchive>(sp));
        }
        else if (archive.Provider != AuditArchiveOptions.None)
        {
            throw new InvalidOperationException($"Unknown Audit:Archive:Provider '{archive.Provider}'. Use None or Blob.");
        }
        services.TryAddSingleton<SemanticAnswerCache>();
        services.TryAddSingleton<IFeedbackStore, InMemoryFeedbackStore>();
        services.TryAddSingleton<FeedbackService>();
        return services;
    }
}
