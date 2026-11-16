namespace PriorAuthBot.Core.Usage;

/// <summary>
/// Token budgets, prices, and spend alerts (chapter 13). The limits are per user per UTC day, and
/// apply to every model call a request makes, whichever endpoint, tool, or agent made it.
/// </summary>
public sealed class UsageOptions
{
    public const string SectionName = "Usage";

    /// <summary>
    /// Where the day's usage is counted. <c>InMemory</c> is one process's memory, which is right for a
    /// laptop and wrong for anything with more than one replica: with three replicas, every user gets
    /// three budgets. <c>Postgres</c> (chapter 14) is one shared count in the application database.
    /// </summary>
    public string Ledger { get; set; } = InMemoryLedger;

    /// <summary>Create the ledger's tables at startup. True for local databases; false where the migration job owns the schema.</summary>
    public bool CreateSchema { get; set; } = true;

    public const string InMemoryLedger = "InMemory";
    public const string PostgresLedger = "Postgres";

    /// <summary>Turns the per-user budgets on. Metering and cost metrics run either way.</summary>
    public bool QuotasEnabled { get; set; } = true;

    /// <summary>Tokens (input plus output) one user may use per UTC day.</summary>
    public long DailyTokensPerUser { get; set; } = 200_000;

    /// <summary>
    /// Larger budgets for some groups, by group name, such as <c>"Clinical": 400000</c>. A user gets
    /// the largest budget among the default and their groups.
    /// </summary>
    public Dictionary<string, long> GroupDailyTokens { get; set; } = [];

    /// <summary>
    /// Price per million tokens, by model or deployment name. <c>Default</c> prices any model not
    /// listed. A local model costs nothing per token, but pricing it at the hosted model it replaces
    /// (a shadow price) shows what the same traffic would cost there.
    /// </summary>
    public Dictionary<string, TokenPrice> Prices { get; set; } = [];

    /// <summary>The daily spend, across all users, that the alerts are measured against. 0 turns the alerts off.</summary>
    public decimal DailySpendAlertUsd { get; set; } = 25m;

    /// <summary>Fractions of <see cref="DailySpendAlertUsd"/> that raise an alert, once each per day.</summary>
    public List<double> AlertThresholds { get; set; } = [];

    public IReadOnlyList<double> EffectiveThresholds => AlertThresholds.Count > 0 ? AlertThresholds : [0.5, 0.8, 1.0];

    public TokenPrice PriceFor(string? model)
    {
        if (model is not null && Prices.TryGetValue(model, out var exact))
        {
            return exact;
        }

        return Prices.TryGetValue("Default", out var fallback) ? fallback : TokenPrice.Free;
    }

    public long DailyLimitFor(IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var limit = DailyTokensPerUser;
        foreach (var group in groups)
        {
            if (GroupDailyTokens.TryGetValue(group, out var groupLimit) && groupLimit > limit)
            {
                limit = groupLimit;
            }
        }

        return limit;
    }
}

public sealed class TokenPrice
{
    public static TokenPrice Free { get; } = new();

    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    public decimal Cost(long inputTokens, long outputTokens) =>
        AI.CostModel.Cost(new AI.ModelPrice("configured", InputPerMillion, OutputPerMillion), inputTokens, outputTokens);
}

/// <summary>Thrown before a model call when the user's daily token budget is spent. The API turns it into a 429.</summary>
public sealed class TokenQuotaExceededException(string userId, long used, long limit, DateTimeOffset resetsAt)
    : Exception($"The daily token budget of {limit:N0} is used up. It resets at {resetsAt:HH:mm} UTC.")
{
    public string UserId { get; } = userId;

    public long Used { get; } = used;

    public long Limit { get; } = limit;

    public DateTimeOffset ResetsAt { get; } = resetsAt;
}
