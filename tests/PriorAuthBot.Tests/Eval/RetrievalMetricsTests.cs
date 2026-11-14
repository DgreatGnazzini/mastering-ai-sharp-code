using PriorAuthBot.Eval;
using Xunit;

namespace PriorAuthBot.Tests.Eval;

/// <summary>The metrics are a few lines each, and a wrong one would quietly mislead every chapter after this one.</summary>
public sealed class RetrievalMetricsTests
{
    private static readonly string[] Ranked = ["pto-policy", "remote-work-policy", "expense-reimbursement"];

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(2, 0.5)]
    [InlineData(3, 1.0)]
    public void RecallAtKCountsTheExpectedDocumentsInTheTopK(int k, double expected) =>
        Assert.Equal(expected, RetrievalMetrics.RecallAtK(Ranked, ["pto-policy", "expense-reimbursement"], k));

    [Fact]
    public void ReciprocalRankIsOneOverTheFirstHit()
    {
        Assert.Equal(1.0, RetrievalMetrics.ReciprocalRank(Ranked, ["pto-policy"]));
        Assert.Equal(1.0 / 3, RetrievalMetrics.ReciprocalRank(Ranked, ["expense-reimbursement"]), 6);
        Assert.Equal(0.0, RetrievalMetrics.ReciprocalRank(Ranked, ["sepsis-screening-protocol"]));
    }

    [Fact]
    public void NothingExpectedIsPerfectRecall() => Assert.Equal(1.0, RetrievalMetrics.RecallAtK(Ranked, [], 6));

    [Fact]
    public void ChunksOfTheSameDocumentCountOnce() =>
        Assert.Equal(["pto-policy", "remote-work-policy"], RetrievalMetrics.Documents(["pto-policy", "pto-policy", "remote-work-policy", "pto-policy"]));
}
