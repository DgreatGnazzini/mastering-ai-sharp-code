using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

public class IngestionPipelineTests
{
    private static readonly CallerIdentity Nurse = new("nurse", "Nurse", ["AllStaff", "Clinical"]);
    private static readonly CallerIdentity Claims = new("claims", "Claims", ["AllStaff", "Claims"]);

    [Fact]
    public async Task FirstRunIndexesEverythingExceptDocumentsWithNoAcl()
    {
        await using var host = RagTestHost.Create();
        var report = await host.IngestSamplesAsync();

        Assert.Equal(14, report.Added);
        Assert.Equal(1, report.SkippedNoAcl);
        Assert.Null(await host.Collection.GetAsync(ChunkRecord.MakeKey("draft-layoff-plan", 0), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecondRunWithNoChangesWritesNothing()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();
        var report = await host.IngestSamplesAsync();

        Assert.Equal(new IngestionReport(0, 0, 14, 0, 1, 0), report);
    }

    [Fact]
    public async Task PermissionChangeTakesEffectOnNextSync()
    {
        await using var host = RagTestHost.Create();
        var pipeline = host.Get<IngestionPipeline>();
        var retriever = host.Get<SecureRetriever>();
        var ct = TestContext.Current.CancellationToken;
        const string question = "What are the prior authorization criteria for a lumbar spine MRI?";

        var documents = await LoadSamplesAsync();
        await pipeline.SyncAsync(documents.ToAsyncEnumerable(), ct);
        Assert.Contains(await retriever.SearchAsync(question, Claims, ct), r => r.Record.DocumentId == "prior-auth-lumbar-mri");

        // Compliance removes Claims from the document's ACL in the source system.
        var narrowed = documents.Select(d => d.Id == "prior-auth-lumbar-mri"
            ? d with { Access = d.Access with { AllowedGroups = ["Clinical"] } }
            : d).ToList();
        var report = await pipeline.SyncAsync(narrowed.ToAsyncEnumerable(), ct);

        Assert.Equal(1, report.Updated);
        Assert.DoesNotContain(await retriever.SearchAsync(question, Claims, ct), r => r.Record.DocumentId == "prior-auth-lumbar-mri");
        Assert.Contains(await retriever.SearchAsync(question, Nurse, ct), r => r.Record.DocumentId == "prior-auth-lumbar-mri");
    }

    [Fact]
    public async Task DeletedDocumentsLeaveTheIndex()
    {
        await using var host = RagTestHost.Create();
        var pipeline = host.Get<IngestionPipeline>();
        var ct = TestContext.Current.CancellationToken;

        var documents = await LoadSamplesAsync();
        await pipeline.SyncAsync(documents.ToAsyncEnumerable(), ct);
        var chunkCount = (await host.Collection.GetAsync(ChunkRecord.MakeKey("sepsis-screening-protocol", 0), cancellationToken: ct))!.ChunkCount;

        var report = await pipeline.SyncAsync(documents.Where(d => d.Id != "sepsis-screening-protocol").ToAsyncEnumerable(), ct);

        Assert.Equal(1, report.Removed);
        for (var i = 0; i < chunkCount; i++)
        {
            Assert.Null(await host.Collection.GetAsync(ChunkRecord.MakeKey("sepsis-screening-protocol", i), cancellationToken: ct));
        }
    }

    [Fact]
    public async Task ShorterDocumentLeavesNoStaleChunks()
    {
        await using var host = RagTestHost.Create();
        var pipeline = host.Get<IngestionPipeline>();
        var ct = TestContext.Current.CancellationToken;

        var documents = await LoadSamplesAsync();
        await pipeline.SyncAsync(documents.ToAsyncEnumerable(), ct);

        var shortened = documents.Select(d => d.Id == "pto-policy" ? d with { Text = "# Paid Time Off Policy\n\n## Summary\n\nSee the HR portal." } : d);
        await pipeline.SyncAsync(shortened.ToAsyncEnumerable(), ct);

        Assert.NotNull(await host.Collection.GetAsync(ChunkRecord.MakeKey("pto-policy", 0), cancellationToken: ct));
        Assert.Null(await host.Collection.GetAsync(ChunkRecord.MakeKey("pto-policy", 1), cancellationToken: ct));
    }

    private static async Task<List<SourceDocument>> LoadSamplesAsync() =>
        await MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
}
