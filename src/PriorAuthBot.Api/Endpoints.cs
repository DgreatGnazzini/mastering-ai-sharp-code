using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Search;
using PriorAuthBot.Core.Security;

namespace PriorAuthBot.Api;

public sealed record ChatRequest(string Message);

public sealed record ClassifyRequest(string Question);

public sealed record ClassifyResponse(QuestionIntent Intent, QuestionRoute Route);

public sealed record CatalogSearchRequest(string Query, int? Top = null, CatalogSearchMode? Mode = null);

public sealed record SearchRequest(string Query);

/// <summary>
/// One search result. <paramref name="Score"/> orders the results; the other three show where a hit
/// came from in hybrid search (chapter 8): its cosine similarity, its keyword rank, and the
/// reranker's score, each null when that step did not find or score it.
/// </summary>
public sealed record SearchHit(string DocumentId, string Title, string Section, double Score, double? VectorScore = null, int? KeywordRank = null, double? RerankScore = null, string Link = "");

public sealed record AskRequest(string Question);

internal static class GroupOverageHandling
{
    /// <summary>Group overage means we do not know the caller's groups, so the request fails closed with 403.</summary>
    public static void UseGroupOverageHandling(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (GroupOverageException ex)
            {
                await Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            }
        });
}

internal static class GuardrailHandling
{
    /// <summary>
    /// Chapter 12: a request the guardrails refused is a 422 with PriorAuthBot's own message, from any
    /// endpoint, and a shield that cannot be reached (when configured to fail closed) is a 503.
    /// Neither echoes the user's text or says which check fired.
    /// </summary>
    public static void UseGuardrailHandling(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (PromptRejectedException ex)
            {
                await Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity).ExecuteAsync(context);
            }
            catch (PromptShieldUnavailableException)
            {
                await Results.Problem("PriorAuthBot's safety checks are unavailable. Try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            }
        });
}

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
