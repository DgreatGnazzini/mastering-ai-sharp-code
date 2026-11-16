using PriorAuthBot.Core.Classification;

namespace PriorAuthBot.Api;

public sealed record ChatRequest(string Message);

public sealed record ClassifyRequest(string Question);

public sealed record ClassifyResponse(QuestionIntent Intent, QuestionRoute Route);

/// <summary>Per-user request limits for every model-backed endpoint (chapter 2).</summary>
public sealed class UserRateLimitOptions
{
    public const string SectionName = "RateLimiting";
    public const string PolicyName = "per-user";

    /// <summary>Requests a user can make in a burst.</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Sustained requests per minute per user.</summary>
    public int RequestsPerMinute { get; set; } = 10;
}
