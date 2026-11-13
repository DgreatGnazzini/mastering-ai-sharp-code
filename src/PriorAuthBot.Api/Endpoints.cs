using PriorAuthBot.Core.Identity;

namespace PriorAuthBot.Api;

public sealed record ChatRequest(string Message);

public sealed record SearchRequest(string Query);

public sealed record SearchHit(string DocumentId, string Title, string Section, double Score);

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
