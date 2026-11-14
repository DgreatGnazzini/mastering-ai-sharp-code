using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Retrieval.Keyword;
using PriorAuthBot.Core.Retrieval.Reranking;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Hybrid retrieval (chapter 8) with the offline embedder and the in-memory store: the keyword side
/// finds codes, respects the same access filter as the vector side, and is gated so one shared word
/// is not enough; reranking reorders and can drop; query rewriting adds queries but not access.
/// </summary>
public sealed class HybridRetrievalTests
{
    private static readonly CallerIdentity Claims = new("claims", "Claims", ["AllStaff", "Claims"]);
    private static readonly CallerIdentity Contractor = new("contractor", "Contractor", ["Contractors"]);

    [Fact]
    public async Task KeywordSideFindsABillingCode()
    {
        await using var host = await HostAsync();

        var results = await host.Get<SecureRetriever>().SearchAsync("CPT 72148", Claims, TestContext.Current.CancellationToken);

        Assert.Equal("prior-auth-lumbar-mri", results[0].Record.DocumentId);
        Assert.Equal(1, results[0].KeywordRank);
    }

    [Fact]
    public async Task KeywordSideAppliesTheAccessFilterInsideTheQuery()
    {
        await using var host = await HostAsync();

        var matches = await host.Get<IKeywordSearch>().SearchAsync("CPT 72148 lumbar MRI prior authorization", Contractor.Groups, 50, TestContext.Current.CancellationToken);

        Assert.All(matches, m => Assert.True(Contractor.IsInAnyGroup(m.AllowedGroups), $"{m.ChunkId} leaked."));
        Assert.DoesNotContain(matches, m => m.DocumentId == "prior-auth-lumbar-mri");
    }

    [Fact]
    public async Task OneSharedWordDoesNotPassTheKeywordGate()
    {
        var terms = Core.Search.KeywordTokenizer.QueryTerms("When must a positive sepsis screen be escalated to a physician?");

        Assert.True(SecureRetriever.TermCoverage(terms, "A physician reviewer signs every denial.") < 0.5);
        Assert.True(SecureRetriever.TermCoverage(terms, "Escalate a positive sepsis screen to the physician within 15 minutes.") >= 0.5);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task VectorModeIgnoresKeywords()
    {
        await using var host = await HostAsync(new() { ["Retrieval:Mode"] = "Vector" });

        var results = await host.Get<SecureRetriever>().SearchAsync("CPT 72148", Claims, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Null(r.KeywordRank));
    }

    [Fact]
    public async Task RerankerReordersAndDropsBelowItsThreshold()
    {
        await using var host = await HostAsync();
        var options = Options.Create(new RetrievalOptions
        {
            MinScore = GoldenSet.Load().Offline.MinScore,
            Rerank = new RerankOptions { Provider = "Test", MinScore = 0.5 },
        });

        // A reranker that likes only the turnaround times section.
        var reranker = new FuncReranker(p => p.Contains("5 business days", StringComparison.Ordinal) ? 1 : 0);
        var retriever = new SecureRetriever(host.Collection, host.Get<IEmbeddingGenerator<string, Embedding<float>>>(), options,
            NullLogger<SecureRetriever>.Instance, host.Get<IKeywordSearch>(), reranker);

        var results = await retriever.SearchAsync("How long does a lumbar MRI prior authorization take?", Claims, TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.Contains("Turnaround", only.Record.Section, StringComparison.Ordinal);
        Assert.Equal(1, only.RerankScore);
    }

    [Fact]
    public async Task RewrittenQueriesAreSearchedWithTheCallersGroups()
    {
        await using var host = await HostAsync();
        var model = new RecordingChatClient(_ => """{"queries":["lumbar spine MRI prior authorization criteria", "CPT 72148"]}""");
        var options = Options.Create(new RetrievalOptions
        {
            MinScore = GoldenSet.Load().Offline.MinScore,
            QueryRewrite = new QueryRewriteOptions { Enabled = true, Count = 2 },
        });
        var retriever = new SecureRetriever(host.Collection, host.Get<IEmbeddingGenerator<string, Embedding<float>>>(), options,
            NullLogger<SecureRetriever>.Instance, host.Get<IKeywordSearch>(), rewriter: new QueryRewriter(model));

        var claims = await retriever.SearchAsync("back scan approval rules", Claims, TestContext.Current.CancellationToken);
        var contractor = await retriever.SearchAsync("back scan approval rules", Contractor, TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(claims, r => r.Record.DocumentId == "prior-auth-lumbar-mri");
        Assert.DoesNotContain(contractor, r => r.Record.DocumentId == "prior-auth-lumbar-mri");
    }

    [Fact]
    public async Task FailedRewriteFallsBackToTheQuestion()
    {
        var model = new RecordingChatClient(_ => "not json");

        var queries = await new QueryRewriter(model).ExpandAsync("Can I work remotely?", 3, TestContext.Current.CancellationToken);

        Assert.Equal(["Can I work remotely?"], queries);
    }

    [Fact]
    public async Task LlmRerankerScoresMissingPassagesAsZero()
    {
        var model = new RecordingChatClient(_ => """{"scores":[{"passage":2,"score":3},{"passage":9,"score":3}]}""");

        var scores = await new LlmReranker(model).ScoreAsync("q", ["a", "b", "c"], TestContext.Current.CancellationToken);

        Assert.Equal([0d, 3d, 0d], scores);
    }

    [Fact]
    public async Task CrossEncoderPairsTheQueryWithEachPassage()
    {
        // The fake model scores a passage by its token count: token type 1, unmasked. See its README.
        using var reranker = new CrossEncoderReranker(Path.Combine(AppContext.BaseDirectory, "Reranker", "fake-cross-encoder"));

        var scores = await reranker.ScoreAsync("sepsis screen", ["long passage words", "short", "the physician"], TestContext.Current.CancellationToken);

        // Passage tokens plus the closing [SEP]; the query's tokens and the padding do not count.
        Assert.Equal([4d, 2d, 3d], scores);
    }

    [Fact]
    public void CrossEncoderExplainsHowToGetTheModel()
    {
        var error = Assert.Throws<FileNotFoundException>(() => new CrossEncoderReranker(Path.Combine(Path.GetTempPath(), "no-such-model")));

        Assert.Contains("scripts/get-reranker.sh", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRerankerFailsAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RagTestHost.Create(new Dictionary<string, string?> { ["Retrieval:Rerank:Provider"] = "Magic" }));

        Assert.Contains("Magic", error.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    private static async Task<RagTestHost> HostAsync(Dictionary<string, string?>? settings = null)
    {
        var host = RagTestHost.Create(settings);
        await host.IngestSamplesAsync();
        return host;
    }

    private sealed class FuncReranker(Func<string, double> score) : IReranker
    {
        public string Name => "test";

        public Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>>(passages.Select(score).ToList());
    }
}
