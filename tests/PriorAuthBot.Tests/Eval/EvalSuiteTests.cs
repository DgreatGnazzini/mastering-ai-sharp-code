using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Eval;

/// <summary>
/// The eval project's offline run, inside <c>dotnet test</c>: no model, no network, so every push
/// runs it on all three operating systems. CI also runs the eval project itself, for the reports.
/// </summary>
public sealed class EvalSuiteTests
{
    private static readonly GoldenSet Set = GoldenSet.Load();
    private static readonly EvalSettings Offline = EvalSettings.Parse(["--provider", "Offline"]);

    [Fact]
    public async Task NoConfigurationLeaksOnAnyQuestion()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var configuration in RetrievalConfiguration.Comparison(rerankerModel: null))
        {
            var run = await RunAsync(configuration, ct);
            Assert.True(run.Leaks == 0, string.Join('\n', run.Results.SelectMany(r => r.Leaks)));
        }
    }

    [Fact]
    public async Task TheShippedConfigurationPassesTheOfflineGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await RunAsync(RetrievalConfiguration.Comparison(null).Single(c => c.Name == RetrievalConfiguration.Chapter8), ct);
        var gate = RegressionGate.Check(Baselines.Load(EvalPaths.Baselines), EvalSettings.Offline, run.Summary(), run.Leaks, answers: null);
        Assert.True(gate.Ok, string.Join('\n', gate.Failed));
    }

    [Fact]
    public async Task HybridBeatsVectorOnlyOnExactTerms()
    {
        // Chapter 8's claim, kept true from here on.
        var ct = TestContext.Current.CancellationToken;
        var configurations = RetrievalConfiguration.Comparison(null);
        var vector = await RunAsync(configurations[0], ct);
        var hybrid = await RunAsync(configurations[1], ct);
        Assert.True(hybrid.Summary(c => c.IsExactTerm).RecallAtTop > vector.Summary(c => c.IsExactTerm).RecallAtTop);
    }

    /// <summary>
    /// The worst model there is: it repeats its whole prompt back as the answer. If PriorAuthBot
    /// never puts restricted text in a prompt, even this model cannot leak, which is why a model or
    /// prompt change cannot break access control. That property is what this test pins down.
    /// </summary>
    [Fact]
    public async Task AModelThatEchoesItsPromptStillCannotLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = EvalHost.Create(Offline.Configuration(Set), chatClient: new RecordingChatClient(m => string.Join("\n", m.Select(x => x.Text))));
        await host.IngestSamplesAsync(ct);
        var leaks = await LeakageChecker.ForSamplesAsync(ct);

        var results = await AnswerEvaluation.RunAsync(host, Set, Set.Cases, leaks, judged: null, cancellationToken: ct);

        Assert.Equal(Set.Cases.Count, results.Count);
        Assert.True(host.Prompts.Prompts.Count > 0 || results.All(r => r.Declined));
        var echoed = results.SelectMany(r => leaks.CheckPrompt(r.Case, Set.Caller(r.Case.User), r.Answer)).ToList();
        Assert.True(echoed.Count == 0 && results.All(r => r.Leaks.Count == 0), string.Join('\n', echoed.Concat(results.SelectMany(r => r.Leaks))));
    }

    private static async Task<RetrievalRun> RunAsync(RetrievalConfiguration configuration, CancellationToken ct)
    {
        var settings = Offline.Configuration(Set);
        foreach (var (key, value) in configuration.Settings)
        {
            settings[key] = value;
        }

        await using var host = EvalHost.Create(settings);
        await host.IngestSamplesAsync(ct);
        return await RetrievalEvaluation.RunAsync(configuration, host, Set, ct);
    }
}
