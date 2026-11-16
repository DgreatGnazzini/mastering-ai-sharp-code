using System.Net.Http.Json;
using PriorAuthBot.Api;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Caching;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Usage;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace PriorAuthBot.Tests.Observability;

/// <summary>
/// Chapter 13: the answer cache saves model calls only between callers with exactly the same
/// permission set, and a cached answer is audited like a fresh one.
/// </summary>
public sealed class AnswerCacheTests
{
    private static readonly CallerIdentity Nurse = new("demo-nurse", "Nina Okafor, RN", ["AllStaff", "Clinical"]);
    private static readonly CallerIdentity NurseManager = new("demo-manager", "Grace Lindqvist, RN", ["Clinical", "AllStaff"]);
    private static readonly CallerIdentity Staff = new("demo-staff", "A staff member", ["AllStaff"]);
    private static readonly CallerIdentity Contractor = new("demo-contractor", "Casey Lin", ["Contractors"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void APartitionIsTheExactPermissionSet()
    {
        var both = PermissionPartition.For(["AllStaff", "Clinical"], "m", "p", 0);

        Assert.Equal(both, PermissionPartition.For(["Clinical", "AllStaff", "Clinical"], "m", "p", 0));
        Assert.NotEqual(both, PermissionPartition.For(["AllStaff"], "m", "p", 0));
        Assert.NotEqual(both, PermissionPartition.For(["AllStaff", "Clinical", "HR"], "m", "p", 0));
        Assert.NotEqual(both, PermissionPartition.For(["AllStaff", "Clinical"], "other-model", "p", 0));
        Assert.NotEqual(both, PermissionPartition.For(["AllStaff", "Clinical"], "m", "other-prompt", 0));
        Assert.NotEqual(both, PermissionPartition.For(["AllStaff", "Clinical"], "m", "p", 1));
    }

    [Fact]
    public async Task ARepeatedQuestionIsAnsweredWithoutAModelCall()
    {
        await using var host = await HostAsync();
        var rag = host.Get<RagAnswerService>();

        var first = await rag.AskAsync("Can I work remotely?", Nurse, Ct);
        var calls = ModelCalls(host);
        var again = await rag.AskAsync("can i work remotely", Nurse, Ct);

        Assert.Equal(calls, ModelCalls(host));
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task SomeoneWithTheSamePermissionsSharesTheAnswer()
    {
        await using var host = await HostAsync();
        var rag = host.Get<RagAnswerService>();

        await rag.AskAsync("When must a positive sepsis screen be escalated to a physician?", Nurse, Ct);
        var calls = ModelCalls(host);
        await rag.AskAsync("When must a positive sepsis screen be escalated to a physician?", NurseManager, Ct);

        Assert.Equal(calls, ModelCalls(host));
    }

    [Fact]
    public async Task FewerPermissionsNeverGetTheCachedAnswer()
    {
        await using var host = await HostAsync();
        var rag = host.Get<RagAnswerService>();

        var nurse = await rag.AskAsync("When must a positive sepsis screen be escalated to a physician?", Nurse, Ct);
        var staff = await rag.AskAsync("When must a positive sepsis screen be escalated to a physician?", Staff, Ct);
        var contractor = await rag.AskAsync("When must a positive sepsis screen be escalated to a physician?", Contractor, Ct);

        Assert.Contains(nurse.Sources, s => s.DocumentId == "sepsis-screening-protocol");
        Assert.DoesNotContain(staff.Sources, s => s.DocumentId == "sepsis-screening-protocol");
        Assert.True(contractor.Declined);
    }

    [Fact]
    public async Task MorePermissionsDoNotGetTheCachedAnswerEither()
    {
        // Not a leak, but the cached answer could miss a document the second caller may read.
        await using var host = await HostAsync();
        var rag = host.Get<RagAnswerService>();

        await rag.AskAsync("Can I work remotely?", Staff, Ct);
        var calls = ModelCalls(host);
        await rag.AskAsync("Can I work remotely?", Nurse, Ct);

        Assert.Equal(calls + 1, ModelCalls(host));
    }

    [Fact]
    public async Task ChangedDocumentsEmptyTheCache()
    {
        await using var host = await HostAsync();
        var cache = host.Get<SemanticAnswerCache>();
        await host.Get<RagAnswerService>().AskAsync("Can I work remotely?", Nurse, Ct);
        Assert.Equal(1, cache.Count);
        var generation = cache.Generation;

        // One document fewer: the pipeline removes it, and every cached answer goes with it.
        await host.Get<IngestionPipeline>().SyncAsync(Except(MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, Ct), "code-of-conduct"), Ct);

        Assert.Equal(0, cache.Count);
        Assert.Equal(generation + 1, cache.Generation);
    }

    [Fact]
    public async Task DeclinesAreNotCached()
    {
        await using var host = await HostAsync();
        await host.Get<RagAnswerService>().AskAsync("When must a positive sepsis screen be escalated to a physician?", Contractor, Ct);

        Assert.Equal(0, host.Get<SemanticAnswerCache>().Count);
    }

    [Fact]
    public async Task OffByDefault()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();
        var rag = host.Get<RagAnswerService>();

        await rag.AskAsync("Can I work remotely?", Nurse, Ct);
        var calls = ModelCalls(host);
        await rag.AskAsync("Can I work remotely?", Nurse, Ct);

        Assert.Equal(calls + 1, ModelCalls(host));
    }

    [Fact]
    public async Task TheThresholdDecidesWhetherAParaphraseIsTheSameQuestion()
    {
        const string asked = "Can I work remotely?";
        const string paraphrase = "Am I allowed to work from home?";
        double similarity;
        await using (var probe = await HostAsync())
        {
            similarity = await probe.Get<SemanticAnswerCache>().SimilarityAsync(asked, paraphrase, Ct);
        }

        foreach (var (threshold, expectHit) in new[] { (similarity - 0.01, true), (Math.Min(1.0, similarity + 0.01), false) })
        {
            await using var host = await HostAsync(threshold);
            var rag = host.Get<RagAnswerService>();
            await rag.AskAsync(asked, Nurse, Ct);
            var calls = ModelCalls(host);
            await rag.AskAsync(paraphrase, Nurse, Ct);

            Assert.Equal(expectHit ? calls : calls + 1, ModelCalls(host));
        }
    }

    [Fact]
    public async Task ACachedAnswerIsAuditedWithItsDocuments()
    {
        await using var factory = new CachingFactory();
        await factory.InitializeAsync();
        var nurse = factory.As("nurse");

        var first = await nurse.PostAsJsonAsync("/ask", new AskRequest("When must a positive sepsis screen be escalated to a physician?"), Ct);
        var second = await nurse.PostAsJsonAsync("/ask", new AskRequest("When must a positive sepsis screen be escalated to a physician?"), Ct);

        var log = factory.Services.GetRequiredService<AuditLog>();
        var original = await log.QueryAsync(new AuditQuery(Id: Assert.Single(first.Headers.GetValues("X-Audit-Id"))), Ct).SingleAsync(Ct);
        var cached = await log.QueryAsync(new AuditQuery(Id: Assert.Single(second.Headers.GetValues("X-Audit-Id"))), Ct).SingleAsync(Ct);
        Assert.False(original.AnswerCacheHit);
        Assert.True(cached.AnswerCacheHit);
        Assert.Equal(0, cached.ModelCalls);
        Assert.Equal(original.Response, cached.Response);
        Assert.Contains(cached.Documents, d => d.DocumentId == "sepsis-screening-protocol" && d.Access == AccessKind.Cited);
    }

    private static int ModelCalls(RagTestHost host) => host.Get<ITokenLedger>().Today().ModelCalls;

    private static async Task<RagTestHost> HostAsync(double threshold = 0.97)
    {
        var host = RagTestHost.Create(new Dictionary<string, string?>
        {
            ["AnswerCache:Enabled"] = "true",
            ["AnswerCache:SimilarityThreshold"] = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await host.IngestSamplesAsync();
        return host;
    }

    private static async IAsyncEnumerable<SourceDocument> Except(IAsyncEnumerable<SourceDocument> documents, string documentId)
    {
        await foreach (var document in documents)
        {
            if (document.Id != documentId)
            {
                yield return document;
            }
        }
    }

    private sealed class CachingFactory : ObservabilityFactory
    {
        protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
        {
            ["AnswerCache:Enabled"] = "true",
        };
    }
}
