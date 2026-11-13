using PriorAuthBot.Core.AI;
using Xunit;

namespace PriorAuthBot.Tests;

public class CostModelTests
{
    private static readonly ModelPrice Mini = new("gpt-4.1-mini", 0.40m, 1.60m);

    [Fact]
    public void InputAndOutputArePricedSeparately() =>
        // 2,000 input tokens at $0.40/M plus 300 output tokens at $1.60/M.
        Assert.Equal(0.00128m, CostModel.Cost(Mini, 2_000, 300));

    [Fact]
    public void MonthlyIsPerRequestTimesVolume()
    {
        var estimate = CostModel.Estimate(Mini, 2_000, 300, requestsPerDay: 4_000);

        Assert.Equal(0.00128m * 4_000 * 30, estimate.PerMonth);
    }

    [Fact]
    public void LocalModelsCostNothingPerToken() =>
        Assert.Equal(0m, CostModel.Cost(new ModelPrice("llama3.2:3b", 0, 0), 1_000_000, 1_000_000));

    [Fact]
    public void TokenCountIsCloseToFourCharactersPerTokenForEnglish()
    {
        var text = File.ReadAllText(Path.Combine(Infrastructure.TestPaths.SampleDocuments, "pto-policy.md"));

        var ratio = (double)text.Length / new TokenCounter().Count(text);

        Assert.InRange(ratio, 3.0, 6.0);
    }
}
