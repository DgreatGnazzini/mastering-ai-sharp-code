using System.Security.Claims;
using PriorAuthBot.Api.Tools;
using PriorAuthBot.Core.Agents;
using PriorAuthBot.Core.Agents.Exceptions;

namespace PriorAuthBot.Api.Agents;

public sealed record AgentRequest(string Message, string? ConversationId = null) : PriorAuthBot.Api.Observability.IAuditedRequest
{
    string PriorAuthBot.Api.Observability.IAuditedRequest.AuditText => Message;
}

public sealed record ExceptionStartRequest(string Message) : PriorAuthBot.Api.Observability.IAuditedRequest
{
    string PriorAuthBot.Api.Observability.IAuditedRequest.AuditText => Message;
}

public sealed record ExceptionSubmitRequest(bool Submit);

public sealed record ExceptionDecisionRequest(bool Approve, string? Comment = null);

/// <summary>
/// Chapter 11's endpoints. <c>POST /agent</c> is a conversation with the policy agent. The
/// <c>/exceptions</c> endpoints run the policy exception workflow: the requester starts it and
/// confirms the draft, the approver the HR system names decides it, and nobody else can see it.
/// Every "not yours", "not found", and "not waiting for you" is the same 404.
/// </summary>
internal static class AgentEndpoints
{
    public static void MapAgents(this RouteGroupBuilder api)
    {
        api.MapPost("/agent", async (AgentRequest request, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, PolicyAgentService agent, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2_000)
            {
                return Results.Problem("Send a message of 1 to 2,000 characters.", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await agent.RunAsync(request.Message, request.ConversationId, callers.Create(user, http), cancellationToken);
            return result.Refusal switch
            {
                AgentTurnRefusal.None => Results.Ok(result.Reply),
                AgentTurnRefusal.NotFound => Results.NotFound(),
                AgentTurnRefusal.ConversationFull => Results.Problem("This conversation has reached its turn limit. Start a new one.", statusCode: StatusCodes.Status409Conflict),
                _ => Disabled(),
            };
        });

        api.MapDelete("/agent/conversations/{id}", async (string id, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, PolicyAgentService agent, CancellationToken cancellationToken) =>
        {
            await agent.EndAsync(id, callers.Create(user, http), cancellationToken);
            return Results.NoContent();
        });

        var exceptions = api.MapGroup("/exceptions");

        exceptions.MapPost("/", async (ExceptionStartRequest request, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, ExceptionRequestService workflow, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2_000)
            {
                return Results.Problem("Send a message of 1 to 2,000 characters.", statusCode: StatusCodes.Status400BadRequest);
            }

            return await workflow.StartAsync(request.Message, callers.Create(user, http), cancellationToken) is { } view ? Results.Ok(view) : Disabled();
        });

        // The approver's list. A literal segment, so it wins over /exceptions/{id}.
        exceptions.MapGet("/reviews", async (ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, ExceptionRequestService workflow, CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ReviewsAsync(callers.Create(user, http), cancellationToken)));

        exceptions.MapGet("/{id}", async (string id, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, ExceptionRequestService workflow, CancellationToken cancellationToken) =>
            OkOrNotFound(await workflow.GetAsync(id, callers.Create(user, http), cancellationToken)));

        exceptions.MapPost("/{id}/submit", async (string id, ExceptionSubmitRequest request, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, ExceptionRequestService workflow, CancellationToken cancellationToken) =>
            OkOrNotFound(await workflow.SubmitAsync(id, callers.Create(user, http), request.Submit, cancellationToken)));

        exceptions.MapPost("/{id}/decision", async (string id, ExceptionDecisionRequest request, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, ExceptionRequestService workflow, CancellationToken cancellationToken) =>
        {
            if (request.Comment is { Length: > 500 })
            {
                return Results.Problem("A comment is at most 500 characters.", statusCode: StatusCodes.Status400BadRequest);
            }

            return OkOrNotFound(await workflow.DecideAsync(id, callers.Create(user, http), request.Approve, request.Comment, cancellationToken));
        });

        exceptions.MapPost("/{id}/withdraw", async (string id, ClaimsPrincipal user, HttpRequest http, ToolCallerFactory callers, ExceptionRequestService workflow, CancellationToken cancellationToken) =>
            OkOrNotFound(await workflow.WithdrawAsync(id, callers.Create(user, http), cancellationToken)));
    }

    private static IResult OkOrNotFound(ExceptionRunView? view) => view is null ? Results.NotFound() : Results.Ok(view);

    private static IResult Disabled() =>
        Results.Problem("Agents are turned off. Ask PriorAuthBot's owners when they will be back.", statusCode: StatusCodes.Status503ServiceUnavailable);
}
