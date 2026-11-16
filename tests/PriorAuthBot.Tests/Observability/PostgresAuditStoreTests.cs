using Npgsql;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Observability;

/// <summary>
/// Chapter 13: the audit table in PostgreSQL refuses updates, deletes inside the retention period,
/// and truncation, whoever asks; and appends from many writers at once still make one valid chain.
/// Runs with PRIORAUTHBOT_TEST_POSTGRES or PRIORAUTHBOT_TEST_TESTCONTAINERS=true, like the other pgvector tests.
/// </summary>
[Collection(nameof(PostgresIntegrationTests))]
public sealed class PostgresAuditStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppendsFormAVerifiableChain()
    {
        await using var data = DataSource();
        var store = new PostgresAuditStore(data, createSchema: true);

        var first = await store.AppendAsync(AuditChainTests.Sample(1) with { Id = NewId() }, Ct);
        var second = await store.AppendAsync(AuditChainTests.Sample(2) with { Id = NewId() }, Ct);

        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.Equal(first.Hash, second.PreviousHash);
        var read = await store.QueryAsync(new AuditQuery(Id: second.Id), Ct).SingleAsync(Ct);
        Assert.Equal(second, read with { Groups = second.Groups, Documents = second.Documents });
        Assert.Equal(second.Hash, AuditChain.ComputeHash(read));
        Assert.True((await VerifyAsync(store)).Valid);
    }

    [Fact]
    public async Task ConcurrentWritersStillMakeOneChain()
    {
        await using var data = DataSource();
        var writers = Enumerable.Range(0, 4).Select(_ => new PostgresAuditStore(data, createSchema: true)).ToList();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => writers[i % 4].AppendAsync(AuditChainTests.Sample(i) with { Id = NewId() }, Ct)));

        var result = await VerifyAsync(writers[0]);
        Assert.True(result.Valid, result.Problem);
    }

    [Theory]
    [InlineData("UPDATE priorauthbot_audit SET record = '{}' WHERE sequence = (SELECT max(sequence) FROM priorauthbot_audit)", "cannot be changed")]
    [InlineData("DELETE FROM priorauthbot_audit WHERE sequence = (SELECT max(sequence) FROM priorauthbot_audit)", "inside the retention period")]
    [InlineData("TRUNCATE priorauthbot_audit", "cannot be truncated")]
    public async Task TheTableRefusesToForget(string sql, string message)
    {
        await using var data = DataSource();
        var store = new PostgresAuditStore(data, createSchema: true);
        await store.AppendAsync(AuditChainTests.Sample(1) with { Id = NewId() }, Ct);

        // Even the table's owner, which the test connects as, is refused: the trigger runs for everyone.
        await using var command = data.CreateCommand(sql);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Contains(message, ex.MessageText, StringComparison.Ordinal);
        Assert.True((await VerifyAsync(store)).Valid);
    }

    private static NpgsqlDataSource DataSource()
    {
        Assert.SkipWhen(LiveSettings.Postgres is null, "Set PRIORAUTHBOT_TEST_POSTGRES (or PRIORAUTHBOT_TEST_TESTCONTAINERS=true) to run against PostgreSQL.");
        return NpgsqlDataSource.Create(LiveSettings.Postgres!);
    }

    private static string NewId() => Guid.CreateVersion7().ToString("N");

    private static async Task<AuditVerification> VerifyAsync(PostgresAuditStore store) =>
        AuditChain.Verify(await store.QueryAsync(new AuditQuery(Limit: int.MaxValue), Ct).ToListAsync(Ct));
}
