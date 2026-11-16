using CommunityToolkit.VectorData.PgVector;
using Microsoft.Extensions.Logging;
using Npgsql;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Data;

/// <summary>What the migration job is told about the database it prepares (chapter 14).</summary>
public sealed class MigrationOptions
{
    public const string SectionName = "Migrate";

    /// <summary>
    /// The PostgreSQL role the API signs in as. With Entra ID authentication it is the name of the
    /// API's managed identity, such as <c>id-priorauthbot-api</c>; locally, any login role.
    /// </summary>
    public string? AppRole { get; set; }

    /// <summary>
    /// The object (principal) ID of the API's managed identity. When set, the job creates the Entra
    /// ID role for it with <c>pgaadauth_create_principal_with_oid</c>, which only an Entra administrator of
    /// the server can run, connected to the <c>postgres</c> database.
    /// </summary>
    public string? AppPrincipalId { get; set; }
}

/// <summary>
/// Chapter 14: the schema, run by a separate job before a new revision starts, as a separate identity.
/// The migration identity is the server's Entra administrator and owns every table. The API's
/// identity gets exactly the rights it uses: append and read the audit log, read and write the index
/// and the ledger. It cannot create, alter, or drop anything, or turn off the audit log's triggers.
/// </summary>
/// <param name="dataSource">The application database, signed in as the migration identity.</param>
/// <param name="forDatabase">Builds a data source for another database on the same server, with the same sign-in.</param>
/// <param name="logger">Where each step is logged.</param>
public sealed partial class DatabaseMigrator(NpgsqlDataSource dataSource, Func<string, NpgsqlDataSource> forDatabase, ILogger<DatabaseMigrator> logger)
{
    /// <summary>The group role chapter 13's scripts/audit-roles.sql defines for the API.</summary>
    public const string ApiGroupRole = "priorauthbot_api";

    public async Task<IReadOnlyList<string>> RunAsync(MigrationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var steps = new List<string>();

        // 1. Extensions. On Azure Database for PostgreSQL, the server parameter azure.extensions must
        //    list VECTOR first (infra/modules/postgres.bicep does that).
        await ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken).ConfigureAwait(false);
        steps.Add("pgvector extension");

        // 2. The audit log (chapter 13), with its triggers, in one transaction under the append lock.
        await new PostgresAuditStore(dataSource, createSchema: true).EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        steps.Add("audit log and its triggers");

        // 3. The shared token ledger (this chapter).
        await ExecuteAsync(PostgresTokenLedger.Schema, cancellationToken).ConfigureAwait(false);
        steps.Add("token ledger");

        // 4. The vector index (chapters 6 to 8), created by the connector from ChunkRecord's attributes.
        using (var store = new PostgresVectorStore(dataSource, ownsDataSource: false))
        {
            var collection = store.GetCollection<Guid, ChunkRecord>(VectorStoreServiceCollectionExtensions.CollectionName);
            await collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
        }

        steps.Add("vector index");

        // 5. The API's group role and its grants. Idempotent: every run leaves the same rights.
        await ExecuteAsync($$"""
            DO $$ BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{{ApiGroupRole}}') THEN
                    CREATE ROLE {{ApiGroupRole}} NOLOGIN;
                END IF;
            END $$;
            REVOKE ALL ON priorauthbot_audit, priorauthbot_audit_retention FROM PUBLIC;
            GRANT INSERT, SELECT ON priorauthbot_audit TO {{ApiGroupRole}};
            GRANT SELECT ON priorauthbot_audit_retention TO {{ApiGroupRole}};
            GRANT SELECT, INSERT, UPDATE ON priorauthbot_token_usage TO {{ApiGroupRole}};
            GRANT SELECT, INSERT ON priorauthbot_spend_alerts TO {{ApiGroupRole}};
            GRANT SELECT, INSERT, UPDATE, DELETE ON {{VectorStoreServiceCollectionExtensions.CollectionName}} TO {{ApiGroupRole}};
            GRANT USAGE ON SCHEMA public TO {{ApiGroupRole}};
            """, cancellationToken).ConfigureAwait(false);
        steps.Add($"role {ApiGroupRole} and its grants");

        // 6. The API's own login: its managed identity, a member of the group role and nothing else.
        if (!string.IsNullOrWhiteSpace(options.AppRole))
        {
            var role = QuoteIdentifier(options.AppRole);
            if (!string.IsNullOrWhiteSpace(options.AppPrincipalId))
            {
                await CreateEntraPrincipalAsync(options.AppRole, options.AppPrincipalId, cancellationToken).ConfigureAwait(false);
                steps.Add($"Entra ID role {options.AppRole}");
            }

            await ExecuteAsync($"GRANT {ApiGroupRole} TO {role};", cancellationToken).ConfigureAwait(false);
            steps.Add($"{options.AppRole} is a member of {ApiGroupRole}");
        }

        foreach (var step in steps)
        {
            LogStep(logger, step);
        }

        return steps;
    }

    /// <summary>Entra ID roles are created from the <c>postgres</c> database, by an Entra administrator.</summary>
    private async Task CreateEntraPrincipalAsync(string roleName, string principalId, CancellationToken cancellationToken)
    {
        await using var postgres = forDatabase("postgres");
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT pgaadauth_create_principal_with_oid($1, $2, 'service', false, false)
            WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = $1)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = roleName });
        command.Parameters.Add(new NpgsqlParameter { Value = principalId });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A role name in double quotes, so a name with dashes (every managed identity's) is one identifier.</summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Migration step done: {Step}")]
    private static partial void LogStep(ILogger logger, string step);
}
