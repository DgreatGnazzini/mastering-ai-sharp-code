using PriorAuthBot.Eval;
using Xunit;

namespace PriorAuthBot.Tests.Eval;

public sealed class RegressionGateTests
{
    private static readonly Baselines Baseline = new(
        null,
        new Tolerances(Retrieval: 0.02, JudgeScore: 0.25, Share: 0.05),
        new Dictionary<string, ProviderBaseline>
        {
            ["Ollama"] = new(new RetrievalBaseline(RecallAtTop: 0.90, Mrr: 0.80, DeclinesKept: 0.95), new Dictionary<string, double> { ["Groundedness"] = 4.0 }),
        });

    [Fact]
    public void PassesAtTheBaseline() =>
        Assert.True(RegressionGate.Check(Baseline, "Ollama", Retrieval(0.90, 0.80), 0, Answers(4.0)).Ok);

    [Fact]
    public void PassesWithinTheTolerance() =>
        Assert.True(RegressionGate.Check(Baseline, "Ollama", Retrieval(0.89, 0.79), 0, Answers(3.8)).Ok);

    [Fact]
    public void FailsOnARetrievalRegression()
    {
        var gate = RegressionGate.Check(Baseline, "Ollama", Retrieval(0.85, 0.80), 0, Answers(4.0));
        Assert.False(gate.Ok);
        Assert.Contains(gate.Failed, f => f.StartsWith("Recall@6", StringComparison.Ordinal));
    }

    [Fact]
    public void FailsOnAGroundednessRegression()
    {
        var gate = RegressionGate.Check(Baseline, "Ollama", Retrieval(0.90, 0.80), 0, Answers(3.6));
        Assert.Contains(gate.Failed, f => f.StartsWith("Groundedness", StringComparison.Ordinal));
    }

    [Fact]
    public void OneLeakFailsEvenWithNoBaselineAtAll()
    {
        var empty = new Baselines(null, new Tolerances(), []);
        Assert.False(RegressionGate.Check(empty, "Ollama", Retrieval(1, 1), retrievalLeaks: 1, answers: null).Ok);
        Assert.False(RegressionGate.Check(empty, "Ollama", Retrieval(1, 1), 0, Answers(5, leaks: 1)).Ok);
    }

    [Fact]
    public void AMetricNoJudgeCouldScoreFails()
    {
        // A judge that stops answering in the expected format must not look like a pass.
        var answers = new AnswerSummary(10, 0, new Dictionary<string, double>(), 0, 0, 0, 0, 0);
        Assert.False(RegressionGate.Check(Baseline, "Ollama", Retrieval(0.9, 0.8), 0, answers).Ok);
    }

    [Fact]
    public void TheCheckedInBaselinesLoad()
    {
        var baselines = Baselines.Load(EvalPaths.Baselines);
        Assert.True(baselines.Tolerance.Retrieval is > 0 and < 0.1);
        Assert.True(baselines.For("Offline")?.Retrieval is not null, "eval/baselines.json needs an Offline entry for the push gate.");
    }

    private static RetrievalSummary Retrieval(double recall, double mrr) => new("Hybrid", 80, recall, recall, mrr, 20, 20, 0);

    private static AnswerSummary Answers(double groundedness, int leaks = 0) =>
        new(30, 20, new Dictionary<string, double> { ["Groundedness"] = groundedness }, 18, 20, 0, 0, leaks);
}
