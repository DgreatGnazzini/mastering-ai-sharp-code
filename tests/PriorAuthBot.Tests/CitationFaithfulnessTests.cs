using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using PriorAuthBot.Tests.Evaluation;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>The citation faithfulness evaluator itself, with a scripted judge (chapter 8).</summary>
public sealed class CitationFaithfulnessTests
{
    private static readonly string[] Sources =
    [
        "Standard requests are decided within 5 business days.",
        "Urgent requests are decided within 72 hours.",
    ];

    [Fact]
    public async Task ScoresTheShareOfSupportedClaims()
    {
        var judge = new RecordingChatClient(_ => """{"verdicts":[{"claim":1,"supported":true},{"claim":2,"supported":false}]}""");

        var result = await Evaluate("Standard requests take 5 business days [1]. Urgent ones take 72 hours [1]. Call us.", judge);

        Assert.Equal(0.5, result.Get<NumericMetric>(CitationFaithfulnessEvaluator.FaithfulnessMetricName).Value);
        Assert.Equal(2.0 / 3, result.Get<NumericMetric>(CitationFaithfulnessEvaluator.CoverageMetricName).Value!.Value, 3);

        // The judge sees each claim next to the one source it cites, not all of them.
        var prompt = judge.Requests.Single()[1].Text;
        Assert.Contains("Claim 2: Urgent ones take 72 hours.\nCited source [1]: Standard requests", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerWithNoCitationsGetsNoFaithfulnessScore()
    {
        var result = await Evaluate("Standard requests take 5 business days.", new ThrowingChatClient());

        Assert.Null(result.Get<NumericMetric>(CitationFaithfulnessEvaluator.FaithfulnessMetricName).Value);
        Assert.Equal(0, result.Get<NumericMetric>(CitationFaithfulnessEvaluator.CoverageMetricName).Value);
    }

    private static ValueTask<EvaluationResult> Evaluate(string answer, IChatClient judge) =>
        new CitationFaithfulnessEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, "question")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
            new ChatConfiguration(judge),
            [new CitationSourcesContext(Sources)],
            TestContext.Current.CancellationToken);
}
