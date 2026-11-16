using System.Security.Claims;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Feedback;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Api.Observability;

public sealed record FeedbackRequest(string AuditId, bool Helpful, string? Comment = null);

public sealed record UsageResponse(DateOnly Day, long TokensUsed, long DailyLimit, long Remaining, int ModelCalls, decimal Cost, DateTimeOffset ResetsAt);

/// <summary>
/// Chapter 13's endpoints. Every user can see their own token budget and rate their own answers.
/// Only the audit reader role can read the audit log, check its chain, see everyone's spend, and
/// export feedback, and each of those reads is itself an audit record.
/// </summary>
internal static class ObservabilityEndpoints
{
    public const string AuditReaderPolicy = "audit-reader";

    public static void MapObservability(this RouteGroupBuilder api)
    {
        api.MapGet("/usage", (ClaimsPrincipal user, CallerIdentityFactory identities, ITokenLedger ledger, IOptionsMonitor<UsageOptions> options) =>
        {
            var caller = identities.Create(user);
            var usage = ledger.ForUser(caller.UserId);
            var limit = options.CurrentValue.DailyLimitFor(caller.Groups);
            return Results.Ok(new UsageResponse(usage.Day, usage.TotalTokens, limit, Math.Max(0, limit - usage.TotalTokens), usage.ModelCalls, usage.Cost, ledger.ResetsAt));
        });

        api.MapPost("/feedback", async (FeedbackRequest request, ClaimsPrincipal user, CallerIdentityFactory identities, FeedbackService feedback, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.AuditId) || request.AuditId.Length > 64 || request.Comment is { Length: > FeedbackService.MaxCommentLength })
            {
                return Results.Problem($"Send the answer's audit ID and a comment of at most {FeedbackService.MaxCommentLength} characters.", statusCode: StatusCodes.Status400BadRequest);
            }

            // The same 404 for "no such answer" and "someone else's answer".
            return await feedback.SubmitAsync(request.AuditId, identities.Create(user), request.Helpful, request.Comment, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        var audit = api.MapGroup("/audit").RequireAuthorization(AuditReaderPolicy).WithMetadata(OmitResponseFromAudit.Instance);

        // "Every PHI document this user's questions touched last month."
        audit.MapGet("/users/{userId}/documents", async (string userId, DateTimeOffset? from, DateTimeOffset? to, bool? phiOnly, AuditLog log, CancellationToken cancellationToken) =>
        {
            var (start, end) = Period(from, to);
            return Results.Ok(await log.DocumentsTouchedAsync(userId, start, end, phiOnly ?? false, cancellationToken));
        });

        audit.MapGet("/records", async (string? userId, string? id, DateTimeOffset? from, DateTimeOffset? to, int? limit, AuditLog log, CancellationToken cancellationToken) =>
        {
            var (start, end) = Period(from, to);
            var records = new List<AuditRecord>();
            await foreach (var record in log.QueryAsync(new AuditQuery(userId, start, end, id, Math.Clamp(limit ?? 100, 1, 500)), cancellationToken))
            {
                records.Add(record);
            }

            return Results.Ok(records);
        });

        audit.MapGet("/verify", async (AuditLog log, CancellationToken cancellationToken) => Results.Ok(await log.VerifyAsync(cancellationToken)));

        // The cost dashboard's numbers for today, across every user.
        audit.MapGet("/usage", (ITokenLedger ledger) => Results.Ok(ledger.Today()));

        audit.MapGet("/feedback/candidates", async (DateTimeOffset? from, DateTimeOffset? to, FeedbackService feedback, CancellationToken cancellationToken) =>
        {
            var (start, end) = Period(from, to);
            return Results.Ok(await feedback.ExportCandidatesAsync(start, end, cancellationToken));
        });
    }

    /// <summary>The last 30 days unless the caller says otherwise; at most a year at a time.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) Period(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);
        return (start < end.AddDays(-366) ? end.AddDays(-366) : start, end);
    }
}

internal static class UsageLimitHandling
{
    /// <summary>A spent token budget becomes a 429 with Retry-After set to the next reset.</summary>
    public static void UseUsageLimitHandling(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (TokenQuotaExceededException ex) when (!context.Response.HasStarted)
            {
                await Quota(ex, context.RequestServices.GetRequiredService<TimeProvider>()).ExecuteAsync(context);
            }
        });

    public static IResult Quota(TokenQuotaExceededException exception, TimeProvider time)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((exception.ResetsAt - time.GetUtcNow()).TotalSeconds));
        return new RetryAfterResult(seconds, Results.Problem(exception.Message, statusCode: StatusCodes.Status429TooManyRequests));
    }

    private sealed class RetryAfterResult(int seconds, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
