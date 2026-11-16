using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Core;

namespace PriorAuthBot.Api.Hosting;

/// <summary>
/// Chapter 14: the probes Azure Container Apps calls. Liveness says the process is running and is
/// never allowed to depend on anything else, or a database outage would restart every replica.
/// Readiness says this replica can answer: the database answers and the first ingestion pass is done.
/// A new revision gets traffic only once it is ready, so a revision that cannot reach the database
/// never takes a request. Neither probe calls the model: a probe every few seconds on every replica
/// would be the busiest client of the model deployment, and a slow model is not a reason to restart.
/// </summary>
internal static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddPriorAuthBotHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<IngestionReadyCheck>("ingestion", tags: [ReadyTag])
            .AddCheck<PostgresReadyCheck>("postgres", tags: [ReadyTag]);
        return services;
    }

    public static void MapPriorAuthBotHealth(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok("healthy"));
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync });
    }

    /// <summary>The status of each check and which build answered. Never an exception message: the probe is anonymous.</summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            chapter = PriorAuthBotInfo.CurrentChapter,
            revision = Revision(configuration),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        }));
    }

    /// <summary>The git commit the image was built from, which the deploy pipeline checks on the canary.</summary>
    public static string Revision(IConfiguration configuration) => configuration["PriorAuthBot:Revision"] ?? "local";
}

internal sealed class IngestionReadyCheck(IngestionWorker worker) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(worker.FirstRunCompleted.IsCompletedSuccessfully
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The first ingestion pass has not finished."));
}

internal sealed class PostgresReadyCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // No PostgreSQL configured (the in-memory stores on a laptop): nothing to check.
        if (services.GetService<NpgsqlDataSource>() is not { } dataSource)
        {
            return HealthCheckResult.Healthy("not used");
        }

        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL did not answer.");
        }
    }
}
