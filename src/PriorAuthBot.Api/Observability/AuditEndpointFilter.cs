using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Security;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Api.Observability;

/// <summary>A request body that carries the user's own text, which the audit log records as the prompt.</summary>
public interface IAuditedRequest
{
    string AuditText { get; }
}

/// <summary>Endpoint metadata: record that the request happened, but not what it returned (the audit endpoints).</summary>
public sealed class OmitResponseFromAudit
{
    public static readonly OmitResponseFromAudit Instance = new();
}

/// <summary>
/// Chapter 13: one audit record for every request to every authenticated endpoint, written after the
/// handler has produced its result and before that result is sent. If the record cannot be written,
/// the user gets a 503 instead of the result (fail closed), unless Audit:OnWriteFailure says otherwise.
/// Failed and refused requests are recorded too: a blocked injection attempt or a spent budget is
/// exactly what an auditor wants to find.
/// </summary>
internal sealed class AuditEndpointFilter(AuditLog audit, IOptions<AuditOptions> options) : IEndpointFilter
{
    private const int MaxResponseCharacters = 4_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        using var scope = AiRequestContext.Begin(OperationName(http), UserId(http.User), http.User.FindFirst("name")?.Value ?? string.Empty);
        var request = scope.Context;
        http.Response.Headers["X-Audit-Id"] = request.AuditId;
        Activity.Current?.SetTag(PriorAuthBotTelemetry.Tags.AuditId, request.AuditId);
        Activity.Current?.SetTag(PriorAuthBotTelemetry.Tags.Operation, request.Operation);
        if (context.Arguments.OfType<IAuditedRequest>().FirstOrDefault() is { } body)
        {
            request.SetPrompt(body.AuditText);
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            // Record the failure, then let the existing middleware turn it into the right status.
            await TryWriteAsync(request, StatusFor(ex), ex);
            throw;
        }

        var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;
        if (request.Response is null && http.GetEndpoint()?.Metadata.GetMetadata<OmitResponseFromAudit>() is null)
        {
            request.SetResponse(Describe(result));
        }

        if (!await TryWriteAsync(request, status, null) && options.Value.OnWriteFailure != AuditOptions.FailOpen)
        {
            return Results.Problem("PriorAuthBot could not record this request in its audit log, so it is not returning the result. Try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return result;
    }

    private async Task<bool> TryWriteAsync(AiRequestContext request, int status, Exception? error)
    {
        try
        {
            await audit.WriteAsync(request, status, error);
            return true;
        }
#pragma warning disable CA1031 // AuditLog has logged it; the caller decides between fail closed and fail open.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    internal static string UserId(ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value ?? user.Identity?.Name ?? "anonymous";

    internal static string OperationName(HttpContext http) =>
        $"{http.Request.Method} {(http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value}";

    private static int StatusFor(Exception exception) => exception switch
    {
        PromptRejectedException => StatusCodes.Status422UnprocessableEntity,
        TokenQuotaExceededException => StatusCodes.Status429TooManyRequests,
        GroupOverageException => StatusCodes.Status403Forbidden,
        PromptShieldUnavailableException => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>What the user was shown, as JSON, cut to a size an audit record can carry.</summary>
    private static string? Describe(object? result)
    {
        var value = result switch
        {
            IValueHttpResult { Value: { } v } => v,
            IResult => null,
            { } other => other,
            _ => null,
        };
        if (value is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(value, value.GetType(), Json);
        return json.Length <= MaxResponseCharacters ? json : json[..MaxResponseCharacters] + "…";
    }
}
