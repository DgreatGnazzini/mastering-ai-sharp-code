using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pgvector.Npgsql;
using PriorAuthBot.Core.Hosting;

namespace PriorAuthBot.Core.Data;

/// <summary>How PriorAuthBot signs in to PostgreSQL (chapter 14).</summary>
public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    /// <summary>
    /// <c>Password</c>: the connection string carries one (local containers, CI).
    /// <c>Entra</c>: Azure Database for PostgreSQL with Microsoft Entra authentication. The user name
    /// in the connection string is the managed identity's role name and the password is an Entra ID
    /// access token, fetched by the managed identity and refreshed before it expires. There is no
    /// database password anywhere, and the server has password authentication turned off.
    /// </summary>
    public string Authentication { get; set; } = Password;

    /// <summary>How often to fetch a fresh token. Entra tokens live 60 to 90 minutes; a new connection needs a valid one.</summary>
    public TimeSpan TokenRefresh { get; set; } = TimeSpan.FromMinutes(30);

    public const string Password = "Password";
    public const string Entra = "Entra";
}

/// <summary>
/// The one <see cref="NpgsqlDataSource"/> the API shares: the vector store, keyword search, the
/// audit log, and the token ledger all draw connections from the same pool. Before chapter 14 each
/// built its own from the connection string, which is four pools and, with Entra ID, four token
/// caches for one identity.
/// </summary>
public static class PostgresDataSource
{
    public static NpgsqlDataSource Create(string connectionString, PostgresOptions options, Func<TokenCredential>? credential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // pgvector's types, which the vector store connector needs on a data source it did not build.
        builder.UseVector();

        switch (options.Authentication)
        {
            case PostgresOptions.Password:
                break;
            case PostgresOptions.Entra:
                if (!string.IsNullOrEmpty(builder.ConnectionStringBuilder.Password))
                {
                    throw new InvalidOperationException("Postgres:Authentication is Entra, but the connection string has a password. Remove it: the password is an Entra ID token.");
                }

                // Azure Database for PostgreSQL accepts Entra tokens only over TLS.
                if (builder.ConnectionStringBuilder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
                {
                    builder.ConnectionStringBuilder.SslMode = SslMode.Require;
                }

                var tokens = (credential ?? (() => AzureIdentity.CreateCredential(new AzureIdentityOptions())))();
                builder.UsePeriodicPasswordProvider(
                    async (_, cancellationToken) =>
                        (await tokens.GetTokenAsync(new TokenRequestContext([AzureIdentity.PostgresScope]), cancellationToken).ConfigureAwait(false)).Token,
                    options.TokenRefresh,
                    TimeSpan.FromSeconds(10));
                break;
            default:
                throw new InvalidOperationException($"Unknown Postgres:Authentication '{options.Authentication}'. Use Password or Entra.");
        }

        return builder.Build();
    }

    /// <summary>
    /// Registers the shared data source for <c>ConnectionStrings:priorauthbot</c>, built on first use,
    /// with chapter 6's iterative index scans and the sign-in from <see cref="PostgresOptions"/>.
    /// </summary>
    public static IServiceCollection AddPriorAuthBotPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(sp => Create(
            Retrieval.VectorStoreServiceCollectionExtensions.WithIterativeScan(configuration.GetConnectionString("priorauthbot")
                ?? throw new InvalidOperationException("ConnectionStrings:priorauthbot is not set, and a PostgreSQL store is configured.")),
            Options(configuration),
            sp.AzureCredential));
        return services;
    }

    public static PostgresOptions Options(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>() ?? new PostgresOptions();
    }
}
