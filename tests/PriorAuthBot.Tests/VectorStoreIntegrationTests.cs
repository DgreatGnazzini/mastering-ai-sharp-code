using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The same permission checks against a real vector store (chapter 6): no caller sees a chunk
/// outside their groups, writing the same records twice changes nothing, and a caller with no
/// groups gets nothing. Each store gets a subclass that says how to reach it; the tests are
/// identical, which is the point of the abstraction. The store is filled with one record per
/// sample document; chapter 7's ingestion pipeline replaces that with real chunks.
/// </summary>
public abstract class VectorStoreIntegrationTests : IAsyncLifetime
{
    private static readonly CallerIdentity[] Callers =
    [
        new("demo-nurse", "Nurse", ["AllStaff", "Clinical"]),
        new("demo-contractor", "Contractor", ["Contractors"]),
        new("demo-claims", "Claims analyst", ["AllStaff", "Claims"]),
        new("demo-hr", "HR business partner", ["AllStaff", "HR"]),
        new("demo-compliance", "Privacy officer", ["AllStaff", "Compliance"]),
    ];

    private static readonly string[] Questions =
    [
        "How many PTO days do I get?",
        "Can I work remotely?",
        "What are the sepsis screening criteria?",
        "How do I appeal a denied claim?",
        "How do I report a privacy incident?",
        "What happened in incident 2026-031?",
    ];

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
        await Host.Collection.EnsureCollectionExistsAsync(TestContext.Current.CancellationToken);
        await Host.Collection.UpsertAsync(await LoadSamplesAsync(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoCallerSeesAnotherGroupsDocuments()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);
        var retriever = Host.Get<SecureRetriever>();

        foreach (var caller in Callers)
        {
            foreach (var question in Questions)
            {
                var results = await retriever.SearchAsync(question, caller, TestContext.Current.CancellationToken);

                Assert.All(results, r => Assert.True(caller.IsInAnyGroup(r.Record.AllowedGroups), $"{caller.UserId}: {r.Record.ChunkId} leaked."));
            }
        }
    }

    [Fact]
    public async Task EachCallerFindsTheirOwnDocuments()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);
        var nurse = Callers[0];

        var results = await Host.Get<SecureRetriever>().SearchAsync("sepsis screening escalation to the physician", nurse, TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Record.DocumentId == "sepsis-screening-protocol");
    }

    [Fact]
    public async Task WritingTheSameRecordsAgainChangesNothing()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);
        var records = await LoadSamplesAsync();

        // The keys are derived from the document ID and chunk index, so a second write overwrites.
        await Host.Collection.UpsertAsync(records, TestContext.Current.CancellationToken);

        var stored = await Host.Collection.GetAsync(records.Select(r => r.Key), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(records.Count, stored.Count);
        Assert.Equal(records.Select(r => r.ChunkId).Order(), stored.Select(r => r.ChunkId).Order());
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

    /// <summary>One record per sample document, with the document's access list copied onto it. A document nobody may see is not stored.</summary>
    private async Task<List<ChunkRecord>> LoadSamplesAsync()
    {
        var embedder = Host.Get<IEmbeddingGenerator<string, Embedding<float>>>();
        var documents = await MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, TestContext.Current.CancellationToken)
            .Where(d => !d.Access.IsDenyAll)
            .ToListAsync(TestContext.Current.CancellationToken);
        var embeddings = await embedder.GenerateAsync(documents.Select(d => d.Text), cancellationToken: TestContext.Current.CancellationToken);

        return [.. documents.Select((d, i) => new ChunkRecord
        {
            Key = ChunkRecord.MakeKey(d.Id, 0),
            ChunkId = ChunkRecord.MakeChunkId(d.Id, 0),
            DocumentId = d.Id,
            ChunkIndex = 0,
            ChunkCount = 1,
            Title = d.Title,
            PolicyNumber = d.PolicyNumber,
            Text = d.Text,
            SourcePath = Path.GetFileName(d.SourcePath),
            AllowedGroups = [.. d.Access.AllowedGroups],
            Sensitivity = d.Access.Sensitivity,
            Department = d.Department,
            ContainsPhi = d.Access.ContainsPhi,
            Embedding = embeddings[i].Vector,
        })];
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
