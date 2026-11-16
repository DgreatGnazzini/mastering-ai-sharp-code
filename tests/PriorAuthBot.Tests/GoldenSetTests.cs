using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The chapter 7 golden set, run on every push with the offline embedder: no model, no network,
/// same result every time. Each case checks three things: the expected documents reach the
/// context, the "must not see" documents never come back for that user, and questions with no
/// visible answer are declined without calling the model.
/// </summary>
public sealed class GoldenSetTests(GoldenSetTests.IndexFixture fixture) : IClassFixture<GoldenSetTests.IndexFixture>
{
    private static readonly GoldenSet Set = GoldenSet.Load();

    public static TheoryData<string> Cases => GoldenSet.CaseIds();

    [Fact]
    public void GoldenSetHasTwentyQuestionsIncludingPermissionCases()
    {
        Assert.Equal(20, Set.Cases.Count);
        Assert.True(Set.Cases.Count(c => c.Forbidden.Length > 0) >= 8, "At least 8 cases must be must-not-see cases.");
        Assert.True(Set.Cases.Count(c => c.ExpectDecline) >= 4, "At least 4 cases must expect a decline.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExpectedDocumentsReachTheContext(string caseId)
    {
        var goldenCase = Set.Case(caseId);
        var context = await fixture.ContextForAsync(goldenCase);

        foreach (var expected in goldenCase.Expected)
        {
            Assert.Contains(context.Sources, s => s.Record.DocumentId == expected);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NothingTheUserCannotSeeIsRetrieved(string caseId)
    {
        var goldenCase = Set.Case(caseId);
        var caller = Set.Caller(goldenCase.User);
        var retrieved = await fixture.Retriever.SearchAsync(goldenCase.Question, caller, TestContext.Current.CancellationToken);

        foreach (var chunk in retrieved)
        {
            Assert.DoesNotContain(chunk.Record.DocumentId, goldenCase.Forbidden);

            // The general rule behind every must-not-see case: every chunk shares a group with the caller.
            Assert.True(caller.IsInAnyGroup(chunk.Record.AllowedGroups), $"{chunk.Record.ChunkId} leaked to {goldenCase.User}.");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DeclinesExactlyWhenExpected(string caseId)
    {
        var goldenCase = Set.Case(caseId);
        var context = await fixture.ContextForAsync(goldenCase);

        if (goldenCase.ExpectDecline)
        {
            Assert.Empty(context.Sources);
            var answer = await fixture.AskWithoutModelAsync(goldenCase);
            Assert.True(answer.Declined);
            Assert.Equal(RagAnswerService.DeclineMessage, answer.Answer);
        }
        else if (goldenCase.Expected.Length > 0)
        {
            Assert.NotEmpty(context.Sources);
        }
    }

    public sealed class IndexFixture : IAsyncLifetime
    {
        private RagTestHost? _host;

        public SecureRetriever Retriever => Host.Get<SecureRetriever>();

        private RagTestHost Host => _host ?? throw new InvalidOperationException("Fixture not initialized.");

        public async ValueTask InitializeAsync()
        {
            _host = RagTestHost.Create(chatClient: new ThrowingChatClient());
            await _host.IngestSamplesAsync();
        }

        internal async Task<AssembledContext> ContextForAsync(GoldenCase goldenCase)
        {
            var retrieved = await Retriever.SearchAsync(goldenCase.Question, Set.Caller(goldenCase.User), TestContext.Current.CancellationToken);
            return Host.Get<ContextAssembler>().Assemble(retrieved);
        }

        /// <summary>The chat client throws if called, which proves a decline never reached the model.</summary>
        internal Task<RagAnswer> AskWithoutModelAsync(GoldenCase goldenCase) =>
            Host.Get<RagAnswerService>().AskAsync(goldenCase.Question, Set.Caller(goldenCase.User), TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
            }
        }
    }
}
