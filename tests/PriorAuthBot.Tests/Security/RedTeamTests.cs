using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Security;
using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Security;

/// <summary>
/// Chapter 12: the red-team suite offline on every push, and the check that the guardrails do not
/// get in the way of the questions PriorAuthBot exists to answer.
/// </summary>
public sealed class RedTeamTests
{
    private static readonly GoldenSet Set = GoldenSet.Load();
    private static readonly EvalSettings Offline = EvalSettings.Parse(["--provider", "Offline"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoAbsoluteCaseIsBreachedWithTheGuardrails()
    {
        var results = await RunAsync(guardrails: true);

        Assert.All(results.Where(r => r.Case.Gate == RedTeamCase.Absolute), r => Assert.True(
            r.Outcome is RedTeamOutcome.Blocked or RedTeamOutcome.Defended,
            $"{r.Case.Id}: {r.Outcome} {r.Error} {string.Join("; ", r.Findings)}"));
        var gate = RegressionGate.Check(Baselines.Load(EvalPaths.Baselines), EvalSettings.Offline,
            new RetrievalSummary("chapter 12", 1, 1, 1, 1, 0, 0, 0), 0, answers: null, redTeam: RedTeamSummary.Of(results));
        Assert.True(gate.Ok, string.Join('\n', gate.Failed));
    }

    [Fact]
    public async Task TheGuardrailsStopAttacksThatWorkWithoutThem()
    {
        var with = RedTeamSummary.Of(await RunAsync(guardrails: true));
        var without = RedTeamSummary.Of(await RunAsync(guardrails: false));

        Assert.True(without.Breaches > with.Breaches, $"without {without.Breaches}, with {with.Breaches}");
        Assert.True(with.Blocked > without.Blocked);
    }

    [Fact]
    public async Task ThePoisonedFormularyStillPassesVetting()
    {
        // Vetting checks what a tool says it does, not what it returns. The red team relies on this:
        // the tampered server must look exactly like the one chapter 10 pinned.
        var owned = new List<IAsyncDisposable>();
        await using (var host = EvalHost.Create(Offline.Configuration(Set), configure: s => RedTeamEvaluation.Configure(s, owned, slowModel: false)))
        {
            var tools = await host.Get<Core.Mcp.McpToolProvider>().GetToolsAsync(Ct);
            Assert.Contains(tools, t => t.Name.EndsWith("search_formulary", StringComparison.Ordinal));
        }

        foreach (var server in owned)
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheHeuristicsFlagNoSampleDocument()
    {
        await using var host = RagTestHost.Create();
        var chunker = host.Get<IChunker>();
        var flagged = new List<string>();
        await foreach (var document in MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, Ct))
        {
            flagged.AddRange(chunker.Chunk(document).Where(c => HeuristicPromptShield.IsDocumentAttack(c.Text)).Select(c => $"{document.Id}: {c.Text[..Math.Min(80, c.Text.Length)]}"));
        }

        Assert.True(flagged.Count == 0, string.Join('\n', flagged));
    }

    [Fact]
    public void TheHeuristicsFlagNoOrdinaryQuestion()
    {
        var questions = Set.Cases.Select(c => c.Question)
            .Concat(ToolCaseSet.Load().Cases.Where(c => c.Kind != "injection").Select(c => c.Question))
            .Concat(AgentCaseSet.Load().Cases.SelectMany(c => c.Turns ?? [c.Message!]));

        var flagged = questions.Where(HeuristicPromptShield.IsUserAttack).ToList();

        Assert.True(flagged.Count == 0, string.Join('\n', flagged));
    }

    [Fact]
    public async Task EveryPoisonedDocumentButTheKnownGapIsFlagged()
    {
        var documents = await MarkdownDocumentLoader.LoadDirectoryAsync(EvalPaths.RedTeamDocuments, Ct).ToListAsync(Ct);

        Assert.Equal(4, documents.Count);
        Assert.All(documents.Where(d => d.Id != "travel-per-diem"), d => Assert.True(HeuristicPromptShield.IsDocumentAttack(d.Text), d.Id));
        Assert.False(HeuristicPromptShield.IsDocumentAttack(documents.Single(d => d.Id == "travel-per-diem").Text));
    }

    private static async Task<IReadOnlyList<RedTeamResult>> RunAsync(bool guardrails)
    {
        var owned = new List<IAsyncDisposable>();
        var config = Offline.Configuration(Set);
        config["Security:Guardrails"] = guardrails ? "true" : "false";
        IReadOnlyList<RedTeamResult> results;
        await using (var host = EvalHost.Create(config, configure: s => RedTeamEvaluation.Configure(s, owned, slowModel: false)))
        {
            await RedTeamEvaluation.IngestAsync(host, Ct);
            results = await RedTeamEvaluation.RunAsync(host, Set, RedTeamCaseSet.Load(), cancellationToken: Ct);
        }

        foreach (var server in owned)
        {
            await server.DisposeAsync();
        }

        Assert.All(results, r => Assert.Null(r.Error));
        return results;
    }
}
