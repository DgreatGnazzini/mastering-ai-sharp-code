using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Search;

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
