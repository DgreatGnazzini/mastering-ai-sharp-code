using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The same permission checks against PostgreSQL with pgvector, where the group filter is
/// translated to SQL (<c>allowed_groups &amp;&amp; @groups</c>). Runs when PRIORAUTHBOT_TEST_POSTGRES is set,
/// as it is in the CI integration job.
/// </summary>
[Collection(nameof(PostgresIntegrationTests))]
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private static readonly GoldenSet Set = GoldenSet.Load();
    private RagTestHost? _host;

    private RagTestHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (LiveSettings.Postgres is null)
        {
            return;
        }

        _host = RagTestHost.Create(new Dictionary<string, string?>
        {
            ["VectorStore:Provider"] = "Postgres",
            ["ConnectionStrings:priorauthbot"] = LiveSettings.Postgres,
        });
        await Host.Collection.EnsureCollectionDeletedAsync(TestContext.Current.CancellationToken);
        await Host.IngestSamplesAsync();
    }

    [Fact]
    public async Task GoldenSetHoldsOnPgvector()
    {
        Assert.SkipWhen(LiveSettings.Postgres is null, "Set PRIORAUTHBOT_TEST_POSTGRES to run against pgvector.");
        var retriever = Host.Get<SecureRetriever>();

        foreach (var goldenCase in Set.Cases)
        {
            var caller = Set.Caller(goldenCase.User);
            var results = await retriever.SearchAsync(goldenCase.Question, caller, TestContext.Current.CancellationToken);

            Assert.All(results, r => Assert.True(caller.IsInAnyGroup(r.Record.AllowedGroups), $"{goldenCase.Id}: {r.Record.Key} leaked."));
            Assert.All(results, r => Assert.DoesNotContain(r.Record.DocumentId, goldenCase.Forbidden));
            foreach (var expected in goldenCase.Expected)
            {
                Assert.True(results.Any(r => r.Record.DocumentId == expected), $"{goldenCase.Id}: expected {expected}.");
            }

            if (goldenCase.ExpectDecline)
            {
                Assert.True(results.Count == 0, $"{goldenCase.Id}: expected no results, got {results.Count}.");
            }
        }
    }

    [Fact]
    public async Task ResyncIsIdempotentOnPgvector()
    {
        Assert.SkipWhen(LiveSettings.Postgres is null, "Set PRIORAUTHBOT_TEST_POSTGRES to run against pgvector.");

        var report = await Host.IngestSamplesAsync();

        Assert.Equal(0, report.ChunksWritten);
        Assert.Equal(14, report.Unchanged);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
