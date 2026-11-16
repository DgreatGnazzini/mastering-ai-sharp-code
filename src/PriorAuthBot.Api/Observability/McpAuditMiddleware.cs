using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Api.Observability;

/// <summary>
/// The MCP endpoint is not a minimal API route, so the endpoint filter does not run for it. This does
/// the same job around each MCP request: open the request context (so the search inside is recorded
/// by SecureRetriever) and write one audit record. MCP streams its response, so a failed write can
/// only be logged here, not turned into a 503.
/// </summary>
internal sealed class McpAuditMiddleware(RequestDelegate next, AuditLog audit)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        using var scope = AiRequestContext.Begin($"{context.Request.Method} /mcp", AuditEndpointFilter.UserId(context.User), context.User.FindFirst("name")?.Value ?? string.Empty);
        context.Response.Headers["X-Audit-Id"] = scope.Context.AuditId;
        Exception? error = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            try
            {
                await audit.WriteAsync(scope.Context, error is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError, error);
            }
#pragma warning disable CA1031 // Logged by AuditLog; the response has already gone.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
