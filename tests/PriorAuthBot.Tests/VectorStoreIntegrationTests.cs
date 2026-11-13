using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The same permission checks against a real vector store (chapter 6): the golden set, an
/// idempotent resync, and a caller with no groups. Each store gets a subclass that says how to reach
/// it; the tests are identical, which is the point of the abstraction.
/// </summary>
public abstract class VectorStoreIntegrationTests : IAsyncLifetime
{
    private static readonly GoldenSet Set = GoldenSet.Load();
    private RagTestHost? _host;

    protected abstract string? SkipReason { get; }

    protected abstract Dictionary<string, string?> Settings { get; }

    private RagTestHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (SkipReason is not null)
        {
            return;
        }

        _host = RagTestHost.Create(Settings);
        await Host.Collection.EnsureCollectionDeletedAsync(TestContext.Current.CancellationToken);
        await Host.IngestSamplesAsync();
    }

    [Fact]
    public async Task GoldenSetHolds()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);
        var retriever = Host.Get<SecureRetriever>();

        foreach (var goldenCase in Set.Cases)
        {
            var caller = Set.Caller(goldenCase.User);
            var results = await retriever.SearchAsync(goldenCase.Question, caller, TestContext.Current.CancellationToken);

            Assert.All(results, r => Assert.True(caller.IsInAnyGroup(r.Record.AllowedGroups), $"{goldenCase.Id}: {r.Record.ChunkId} leaked."));
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
    public async Task ResyncIsIdempotent()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

        var report = await Host.IngestSamplesAsync();

        Assert.Equal(0, report.ChunksWritten);
        Assert.Equal(14, report.Unchanged);
    }

    [Fact]
    public async Task CallerWithNoGroupsGetsNothing()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

        var results = await Host.Get<SecureRetriever>().SearchAsync("How many PTO days do I get?", new CallerIdentity("no-groups", "Nobody", []), TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>PostgreSQL with pgvector: the group filter becomes <c>"AllowedGroups" &amp;&amp; @groups</c> in SQL.</summary>
[Collection(nameof(PostgresIntegrationTests))]
public sealed class PostgresIntegrationTests : VectorStoreIntegrationTests
{
    protected override string? SkipReason => LiveSettings.Postgres is null ? "Set PRIORAUTHBOT_TEST_POSTGRES to run against pgvector." : null;

    protected override Dictionary<string, string?> Settings => new()
    {
        ["VectorStore:Provider"] = "Postgres",
        ["ConnectionStrings:priorauthbot"] = LiveSettings.Postgres,
    };
}

/// <summary>Qdrant: the group filter becomes a payload filter on an indexed keyword field.</summary>
[Collection(nameof(QdrantIntegrationTests))]
public sealed class QdrantIntegrationTests : VectorStoreIntegrationTests
{
    protected override string? SkipReason => LiveSettings.Qdrant is null ? "Set PRIORAUTHBOT_TEST_QDRANT to run against Qdrant." : null;

    protected override Dictionary<string, string?> Settings => new()
    {
        ["VectorStore:Provider"] = "Qdrant",
        ["VectorStore:Qdrant:Host"] = LiveSettings.Qdrant?.Split(':')[0],
        ["VectorStore:Qdrant:Port"] = LiveSettings.Qdrant?.Split(':')[1],
    };
}
