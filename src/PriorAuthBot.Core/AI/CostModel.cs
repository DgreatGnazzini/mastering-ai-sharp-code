namespace PriorAuthBot.Core.AI;

/// <summary>A model's price per million tokens. Input and output are priced separately, and output costs more.</summary>
public sealed record ModelPrice(string Model, decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>The cost of a workload: tokens per request, times requests.</summary>
public sealed record CostEstimate(string Model, long InputTokens, long OutputTokens, decimal PerRequest, decimal PerMonth);

/// <summary>
/// The cost model from chapter 1: cost = input tokens x input price + output tokens x output price.
/// Chapter 13 feeds it real token counts from every response's usage.
/// </summary>
public static class CostModel
{
    public const int DaysPerMonth = 30;

    public static decimal Cost(ModelPrice price, long inputTokens, long outputTokens)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        return ((inputTokens * price.InputPerMillion) + (outputTokens * price.OutputPerMillion)) / 1_000_000m;
    }

    public static CostEstimate Estimate(ModelPrice price, long inputTokens, long outputTokens, long requestsPerDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestsPerDay);
        var perRequest = Cost(price, inputTokens, outputTokens);
        return new CostEstimate(price!.Model, inputTokens, outputTokens, perRequest, perRequest * requestsPerDay * DaysPerMonth);
    }
}
