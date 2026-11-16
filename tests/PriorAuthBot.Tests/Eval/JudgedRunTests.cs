using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Formats.Html;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Eval;

/// <summary>
/// The judged half of the eval, with a fake judge: no model, but the real evaluators, result
/// store, response cache, and HTML report. A scripted judge makes the plumbing testable on every
/// push; whether a real judge's scores mean anything is the RAG eval job's question.
/// </summary>
public sealed class JudgedRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "priorauthbot-judged-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task JudgesScoreTheSampleAndTheReportIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var set = GoldenSet.Load();
        var settings = EvalSettings.Parse(["--provider", "Offline"]);
        // The model under test: one sentence citing its first source, the shape chapter 8's prompt asks for.
        await using var host = EvalHost.Create(settings.Configuration(set), chatClient: new RecordingChatClient(_ => "The answer is in the first source [1]."));
        await host.IngestSamplesAsync(ct);
        var judge = new RecordingChatClient(messages => messages[0].Text.Contains("You check citations", StringComparison.Ordinal)
            ? """{"verdicts": [{"claim": 1, "supported": true}]}"""
            : "<S0>Looked at the answer.</S0><S1>It answers from the sources.</S1><S2>4</S2>");
        var reporting = DiskBasedReportingConfiguration.Create(
            _directory, AnswerEvaluation.Evaluators(), new ChatConfiguration(judge), enableResponseCaching: true, executionName: "test");
        var cases = set.Cases.Where(c => c.AnswerEval).Take(3).ToList();

        var results = await AnswerEvaluation.RunAsync(host, set, cases, await LeakageChecker.ForSamplesAsync(ct), reporting, cancellationToken: ct);

        var scored = results.Where(r => !r.Declined).ToList();
        Assert.NotEmpty(scored);
        Assert.All(scored, r => Assert.Equal(4, r.Scores["Groundedness"]));
        Assert.All(scored, r => Assert.Equal(1, r.Scores[CitationFaithfulnessEvaluator.FaithfulnessMetricName]));

        var stored = new List<ScenarioRunResult>();
        await foreach (var result in new DiskBasedResultStore(_directory).ReadResultsAsync("test", cancellationToken: ct))
        {
            stored.Add(result);
        }

        Assert.Equal(scored.Count, stored.Count);
        var html = Path.Combine(_directory, "report.html");
        await new HtmlReportWriter(html).WriteReportAsync(stored, ct);
        Assert.True(new FileInfo(html).Length > 1000);

        // A second run with the same requests is answered by the response cache: the judge sees nothing new.
        var calls = judge.Requests.Count;
        await AnswerEvaluation.RunAsync(host, set, cases, await LeakageChecker.ForSamplesAsync(ct), reporting, cancellationToken: ct);
        Assert.Equal(calls, judge.Requests.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
