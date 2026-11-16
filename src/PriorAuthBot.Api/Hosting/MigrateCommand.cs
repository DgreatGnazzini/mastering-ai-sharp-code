using Npgsql;
using PriorAuthBot.Core.Data;
using PriorAuthBot.Core.Hosting;

namespace PriorAuthBot.Api.Hosting;

/// <summary>
/// Chapter 14: <c>dotnet PriorAuthBot.Api.dll migrate</c>. The same image as the API, run by the
/// migration job in Azure Container Apps as the database administrator identity, before a new
/// revision starts. It reads configuration exactly like the API does, prepares the schema and the
/// API's grants, and exits: 0 when every step ran.
/// </summary>
internal static partial class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        using var host = builder.Build();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PriorAuthBot.Migrate");

        var connectionString = configuration.GetConnectionString("priorauthbot")
            ?? throw new InvalidOperationException("ConnectionStrings:priorauthbot is not set.");
        var postgres = PostgresDataSource.Options(configuration);
        var credential = AzureIdentity.CreateCredential(AzureIdentity.Options(configuration));
        NpgsqlDataSource ForDatabase(string database) =>
            PostgresDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString, postgres, () => credential);

        await using var dataSource = PostgresDataSource.Create(connectionString, postgres, () => credential);
        var options = configuration.GetSection(MigrationOptions.SectionName).Get<MigrationOptions>() ?? new MigrationOptions();
        var live = configuration.GetSection(Core.Retrieval.VectorStoreOptions.SectionName).Get<Core.Retrieval.VectorStoreOptions>()?.CollectionName
            ?? Core.Retrieval.VectorStoreServiceCollectionExtensions.CollectionName;
        var migrator = new DatabaseMigrator(dataSource, ForDatabase, host.Services.GetRequiredService<ILogger<DatabaseMigrator>>())
        {
            // Chapter 15: the live collection, plus any being built beside it for a new embedding model.
            Collections = [.. new[] { live }.Concat(options.Collections).Distinct(StringComparer.Ordinal)],
        };
        try
        {
            var steps = await migrator.RunAsync(options);
            LogFinished(logger, steps.Count);
            return 0;
        }
        catch (NpgsqlException ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    [LoggerMessage(EventId = 1402, Level = LogLevel.Information, Message = "Migration finished: {Steps} steps")]
    private static partial void LogFinished(ILogger logger, int steps);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Error, Message = "Migration failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
