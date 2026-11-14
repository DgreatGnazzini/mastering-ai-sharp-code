using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The golden set, run on every push with the offline embedder: no model, no network, same result
/// every time. The leak check runs on all 105 questions and must hold for every one. The expected
/// documents and declines are hard assertions for the 24 questions of chapters 7 and 8; from
/// chapter 9 on, the eval project measures them as recall and gates on its baseline instead, because
/// a crude offline embedder is not expected to get every paraphrase right.
/// </summary>
public sealed class GoldenSetTests(GoldenSetTests.IndexFixture fixture) : IClassFixture<GoldenSetTests.IndexFixture>
{
    private static readonly GoldenSet Set = GoldenSet.Load();

    public static TheoryData<string> Cases => GoldenCases.Ids();

    public static TheoryData<string> Chapter8Cases => GoldenCases.Ids(c => c.Chapter <= 8);

    [Fact]
    public void GoldenSetHasAboutAHundredQuestionsIncludingPermissionCases()
    {
        // Chapter 7's 20, chapter 8's four exact-term questions, and chapter 9's 81.
        Assert.Equal(105, Set.Cases.Count);
        Assert.True(Set.Cases.Count(c => c.IsPermissionCase) >= 20, "At least 20 cases must be must-not-see cases.");
        Assert.True(Set.Cases.Count(c => c.ExpectDecline) >= 20, "At least 20 cases must expect a decline.");
        Assert.All(Set.Cases, c => Assert.Contains(c.Kind, new[] { GoldenCase.Fact, GoldenCase.ExactTerm, GoldenCase.Permission, GoldenCase.OutOfScope }));
        Assert.All(Set.Cases, c => Assert.True(Set.Users.ContainsKey(c.User), $"{c.Id}: unknown user {c.User}."));
        Assert.Equal(Set.Cases.Count, Set.Cases.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void EveryJudgedAnswerHasAGroundTruth()
    {
        // The completeness judge compares the answer with expectedAnswer, so a judged case without one would be scored against nothing.
        Assert.All(Set.Cases.Where(c => c.AnswerEval), c => Assert.False(string.IsNullOrWhiteSpace(c.ExpectedAnswer), $"{c.Id} has no expectedAnswer."));
    }

    [Theory]
    [MemberData(nameof(Chapter8Cases))]
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
    [MemberData(nameof(Chapter8Cases))]
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
