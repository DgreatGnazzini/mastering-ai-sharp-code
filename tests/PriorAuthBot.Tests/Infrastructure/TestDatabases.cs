using Npgsql;
using Testcontainers.PostgreSql;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// Integration tests against a real database with Testcontainers (chapter 9). With
/// PRIORAUTHBOT_TEST_TESTCONTAINERS=true, the first test that asks starts the same pgvector image
/// the CI job used to declare as a service, on a free port, and every test in the run shares it.
/// Testcontainers removes the container when the test process exits. Docker must be running.
/// </summary>
internal static class TestDatabases
{
    /// <summary>The image CI pins; chapter 6 measured iterative scans on this version.</summary>
    public const string PgvectorImage = "pgvector/pgvector:0.8.7-pg17-trixie";

    private static readonly Lazy<string?> PostgresConnection = new(StartPostgres);

    public static bool Enabled => string.Equals(Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_TESTCONTAINERS"), "true", StringComparison.OrdinalIgnoreCase);

    public static string? Postgres => Enabled ? PostgresConnection.Value : null;

    private static string? StartPostgres()
    {
        // Lazy<T> runs this once, from whichever test needs it first; blocking here keeps LiveSettings synchronous.
        var container = new PostgreSqlBuilder(PgvectorImage)
            .WithDatabase("priorauthbot_test")
            .Build();
        container.StartAsync().GetAwaiter().GetResult();

        using var connection = new NpgsqlConnection(container.GetConnectionString());
        connection.Open();
        using var command = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", connection);
        command.ExecuteNonQuery();
        return container.GetConnectionString();
    }
}
