using System.Security.Claims;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Api.Tools;

public sealed record AssistRequest(string Message) : PriorAuthBot.Api.Observability.IAuditedRequest
{
    string PriorAuthBot.Api.Observability.IAuditedRequest.AuditText => Message;
}

/// <summary>
/// Chapter 10's endpoints. <c>POST /assist</c> lets the model use tools. A write comes back as a
/// pending action, and only <c>POST /actions/{id}/approve</c>, sent by the same user, runs it.
/// </summary>
internal static class AssistantEndpoints
{
    public static void MapAssistant(this RouteGroupBuilder api)
    {
        api.MapPost("/assist", async (AssistRequest request, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, AssistantService assistant, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2_000)
            {
                return Results.Problem("Send a message of 1 to 2,000 characters.", statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(await assistant.AskAsync(request.Message, callers.Create(user, http), cancellationToken));
        });

        // The same 404 for "no such action", "someone else's action", "already used", and "expired",
        // so an action ID tells nobody else anything.
        api.MapPost("/actions/{id}/approve", async (string id, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, AssistantService assistant, CancellationToken cancellationToken) =>
        {
            var outcome = id.Length <= 64 ? await assistant.ApproveAsync(id, callers.Create(user, http), cancellationToken) : null;
            return outcome is null ? Results.NotFound() : Results.Ok(outcome);
        });

        api.MapPost("/actions/{id}/reject", (string id, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, AssistantService assistant) =>
            id.Length <= 64 && assistant.Reject(id, callers.Create(user, http)) ? Results.NoContent() : Results.NotFound());
    }
}
