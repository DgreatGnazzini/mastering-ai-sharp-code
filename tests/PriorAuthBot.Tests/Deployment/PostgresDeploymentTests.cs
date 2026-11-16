using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Data;
using PriorAuthBot.Core.Usage;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>
/// Chapter 14, against a real PostgreSQL with pgvector: the shared token ledger adds up charges from
/// several replicas at once and raises each spend alert once; the migration job is idempotent and
/// leaves the API's role able to do its job and nothing more. Runs with PRIORAUTHBOT_TEST_POSTGRES or
/// PRIORAUTHBOT_TEST_TESTCONTAINERS=true, like the other pgvector tests.
/// </summary>
[Collection(nameof(PostgresIntegrationTests))]
public sealed class PostgresDeploymentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThreeReplicasShareOneBudget()
    {
        await using var data = DataSource();
        var day = new FixedClock(new DateTimeOffset(2031, 1, (int)(DateTime.UtcNow.Ticks % 27) + 1, 12, 0, 0, TimeSpan.Zero));
        var options = new Monitor(new UsageOptions { DailySpendAlertUsd = 1m, AlertThresholds = [0.5, 1.0] });
        var replicas = Enumerable.Range(0, 3).Select(_ => new PostgresTokenLedger(data, options, day)).ToList();
        var user = $"user-{Guid.NewGuid():N}";
        await ClearDayAsync(data, day);

        var alerts = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => replicas[i % 3].ChargeAsync(user, 100, 10, 0.02m, Ct).AsTask()));

        var usage = await replicas[0].ForUserAsync(user, Ct);
        Assert.Equal(6_000, usage.InputTokens);
        Assert.Equal(600, usage.OutputTokens);
        Assert.Equal(60, usage.ModelCalls);
        Assert.Equal(1.20m, usage.Cost);

        // $1.20 crossed both thresholds; each was raised by exactly one charge, on one replica.
        Assert.Equal([0.5, 1.0], alerts.SelectMany(a => a).Order());
        Assert.Equal(1, (await replicas[2].TodayAsync(Ct)).Users);
    }

    [Fact]
    public async Task TheMigrationIsIdempotentAndTheApiRoleCanOnlyDoItsJob()
    {
        await using var admin = DataSource();
        var appRole = $"app_{Guid.NewGuid():N}"[..20];
        await using (var create = admin.CreateCommand($"CREATE ROLE {appRole} LOGIN PASSWORD 'test-only'"))
        {
            await create.ExecuteNonQueryAsync(Ct);
        }

        var migrator = new DatabaseMigrator(admin, db => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(LiveSettings.Postgres!) { Database = db }.ConnectionString), NullLogger<DatabaseMigrator>.Instance);
        await migrator.RunAsync(new MigrationOptions { AppRole = appRole }, Ct);
        var steps = await migrator.RunAsync(new MigrationOptions { AppRole = appRole }, Ct);
        Assert.Contains(steps, s => s.Contains(DatabaseMigrator.ApiGroupRole, StringComparison.Ordinal));

        await using var api = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(LiveSettings.Postgres!) { Username = appRole, Password = "test-only" }.ConnectionString);

        // What the API does: append to the audit log, charge the ledger, read the index.
        var appended = await new PostgresAuditStore(api).AppendAsync(Observability.AuditChainTests.Sample(1) with { Id = Guid.NewGuid().ToString("N") }, Ct);
        Assert.True(appended.Sequence > 0);
        await new PostgresTokenLedger(api, new Monitor(new UsageOptions { CreateSchema = false })).ChargeAsync("migration-test", 1, 1, 0m, Ct);
        await ExecuteAsync(api, "SELECT count(*) FROM policy_chunks");

        // What it must not do.
        foreach (var sql in new[]
        {
            "CREATE TABLE priorauthbot_api_was_here (id int)",
            "DELETE FROM priorauthbot_audit",
            "ALTER TABLE priorauthbot_audit DISABLE TRIGGER USER",
            "UPDATE priorauthbot_audit_retention SET retention_days = 365",
            "DROP TABLE policy_chunks",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(api, sql));
            Assert.True(ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or "42501", $"{sql}: {ex.MessageText}");
        }
    }

    private static NpgsqlDataSource DataSource()
    {
        Assert.SkipWhen(LiveSettings.Postgres is null, "Set PRIORAUTHBOT_TEST_POSTGRES (or PRIORAUTHBOT_TEST_TESTCONTAINERS=true) to run against PostgreSQL.");
        return PostgresDataSource.Create(LiveSettings.Postgres!, new PostgresOptions());
    }

    private static async Task ClearDayAsync(NpgsqlDataSource data, TimeProvider day)
    {
        await using (var schema = data.CreateCommand(PostgresTokenLedger.Schema))
        {
            await schema.ExecuteNonQueryAsync(Ct);
        }

        foreach (var table in new[] { "priorauthbot_token_usage", "priorauthbot_spend_alerts" })
        {
            await using var clear = data.CreateCommand($"DELETE FROM {table} WHERE day = $1");
            clear.Parameters.Add(new NpgsqlParameter { Value = DateOnly.FromDateTime(day.GetUtcNow().UtcDateTime), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
            await clear.ExecuteNonQueryAsync(Ct);
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource data, string sql)
    {
        await using var command = data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Monitor(UsageOptions value) : IOptionsMonitor<UsageOptions>
    {
        public UsageOptions CurrentValue => value;

        public UsageOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<UsageOptions, string?> listener) => null;
    }
}
