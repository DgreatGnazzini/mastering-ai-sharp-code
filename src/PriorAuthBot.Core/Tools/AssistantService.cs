using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Mcp;
using PriorAuthBot.Core.Prompts;

namespace PriorAuthBot.Core.Tools;

/// <summary>One tool call the model made, for the trace the API returns.</summary>
public sealed record ToolCallRecord(string Name, string Arguments, string Status);

/// <summary>PriorAuthBot's reply to one request: the answer, the calls behind it, and any write waiting for approval.</summary>
public sealed record AssistantReply(string Answer, IReadOnlyList<ToolCallRecord> ToolCalls, IReadOnlyList<PendingActionView> PendingActions);

/// <summary>What the user sees on the approval card: what will run, with exactly these arguments.</summary>
public sealed record PendingActionView(string Id, string Tool, string Description, IReadOnlyDictionary<string, object?> Arguments, DateTimeOffset ExpiresAt);

/// <summary>The result of an approved action.</summary>
public sealed record ActionOutcome(string Id, string Tool, string Message, object? Result);

/// <summary>
/// PriorAuthBot with tools (chapter 10). The model proposes calls; <see cref="FunctionInvokingChatClient"/>
/// runs the read-only ones and stops at any write, which becomes a <see cref="PendingAction"/>. A
/// write runs only from <see cref="ApproveAsync"/>, with the arguments the user saw, as the user.
/// </summary>
public sealed partial class AssistantService(
    [FromKeyedServices(AiServiceCollectionExtensions.ToolsKey)] IChatClient chatClient,
    PriorAuthBotTools tools,
    PendingActionStore pending,
    ILogger<AssistantService> logger,
    McpToolProvider? mcp = null)
{
    public async Task<AssistantReply> AskAsync(string message, ToolCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(caller);

        var available = await ToolsForAsync(caller, cancellationToken).ConfigureAwait(false);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, PromptLibrary.Get(PromptLibrary.AssistantTools)),
            new(ChatRole.User, message),
        ];
        var options = new ChatOptions
        {
            Tools = [.. available.Select(t => t.ForModel)],
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false,
            Temperature = 0,
        };

        var response = await chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        var calls = Trace(contents);
        var proposed = HoldForApproval(contents, available, caller);

        return new AssistantReply(ComposeAnswer(response.Text, proposed), calls, proposed);
    }

    /// <summary>The model's answer, with PriorAuthBot's own words about any write that waits for approval.</summary>
    public static string ComposeAnswer(string? modelText, IReadOnlyList<PendingActionView> proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var answer = modelText?.Trim() ?? string.Empty;
        if (proposed.Count > 0)
        {
            return $"{(answer.Length > 0 ? answer + "\n\n" : string.Empty)}Nothing has been submitted yet. Approve or cancel: {string.Join("; ", proposed.Select(p => p.Description))}.";
        }

        return answer.Length == 0 ? "I could not complete that request." : answer;
    }

    /// <summary>The trace, in order: every call that ran, and every call that stopped for approval.</summary>
    public static IReadOnlyList<ToolCallRecord> Trace(IEnumerable<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var list = contents as IList<AIContent> ?? contents.ToList();
        var results = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        foreach (var result in list.OfType<FunctionResultContent>())
        {
            results[result.CallId] = result;
        }

        var calls = new List<ToolCallRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in list)
        {
            var call = content switch
            {
                FunctionCallContent c => c,
                ToolApprovalRequestContent { ToolCall: FunctionCallContent c } => c,
                _ => null,
            };
            if (call is null || !seen.Add(call.CallId))
            {
                continue;
            }

            var status = results.TryGetValue(call.CallId, out var r) ? (r.Exception is null ? "ran" : "failed") : "waiting for approval";
            calls.Add(new ToolCallRecord(call.Name, Serialize(call.Arguments), status));
        }

        return calls;
    }

    /// <summary>
    /// Turns every approval request in a response into a <see cref="PendingAction"/> for the caller,
    /// with a snapshot of the arguments. Chapter 11's agent uses this too, so a write the agent
    /// proposes waits for the same approval as one from <see cref="AskAsync"/>.
    /// </summary>
    public IReadOnlyList<PendingActionView> HoldForApproval(IEnumerable<AIContent> contents, IReadOnlyList<ToolEntry> available, ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(caller);
        var proposed = new List<PendingActionView>();
        foreach (var request in contents.OfType<ToolApprovalRequestContent>())
        {
            if (request.ToolCall is not FunctionCallContent call || available.FirstOrDefault(t => t.Name == call.Name) is not { IsWrite: true })
            {
                continue;
            }

            // Keep a copy of the exact arguments. Approval runs these, not whatever the model says next.
            var arguments = Snapshot(call.Arguments);
            var action = pending.Add(caller.UserId, call.Name, arguments, PriorAuthBotTools.Describe(call.Name, arguments));
            proposed.Add(new PendingActionView(action.Id, action.ToolName, action.Description, action.Arguments, action.ExpiresAt));
            LogProposed(logger, caller.UserId, call.Name, action.Id);
        }

        return proposed;
    }

    /// <summary>
    /// Runs a write the user approved. Returns null when there is no such action for this user, it
    /// was already used, or it expired; the endpoint maps all of those to the same 404.
    /// </summary>
    public async Task<ActionOutcome?> ApproveAsync(string actionId, ToolCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(caller);

        var action = pending.Take(actionId, caller.UserId);
        if (action is null)
        {
            return null;
        }

        // Rebuild the tools for the approving user: the action runs as them, never as whoever proposed it.
        var tool = (await ToolsForAsync(caller, cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => t.Name == action.ToolName && t.IsWrite)
            ?? throw new InvalidOperationException($"The tool {action.ToolName} is no longer available.");
        var arguments = new AIFunctionArguments(action.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value))
        {
            Context = new Dictionary<object, object?> { [PriorAuthBotTools.ApprovedActionKey] = action.Id },
        };

        var result = await tool.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        LogApproved(logger, caller.UserId, action.ToolName, action.Id);
        return new ActionOutcome(action.Id, action.ToolName, $"Done: {action.Description}", result);
    }

    /// <summary>Cancels a proposed write. Returns false when there is no such action for this user.</summary>
    public bool Reject(string actionId, ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var action = pending.Take(actionId, caller.UserId);
        if (action is not null)
        {
            LogRejected(logger, caller.UserId, action.ToolName, action.Id);
        }

        return action is not null;
    }

    /// <summary>PriorAuthBot's own tools for this caller, then the vetted MCP tools.</summary>
    public async Task<IReadOnlyList<ToolEntry>> ToolsForAsync(ToolCaller caller, CancellationToken cancellationToken = default)
    {
        var own = tools.For(caller);
        var external = mcp is null ? [] : await mcp.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        return [.. own, .. external];
    }

    private static Dictionary<string, object?> Snapshot(IDictionary<string, object?>? arguments) =>
        arguments is null
            ? []
            : arguments.ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.SerializeToElement(kv.Value, AIJsonUtilities.DefaultOptions), StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private static string Serialize(IDictionary<string, object?>? arguments) =>
        JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), Compact);

    // Log the tool and the action ID, never the arguments: they are the user's words and can hold PHI.
    [LoggerMessage(Level = LogLevel.Information, Message = "Proposed {Tool} for {UserId}, waiting for approval (action {ActionId}).")]
    private static partial void LogProposed(ILogger logger, string userId, string tool, string actionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{UserId} approved {Tool} (action {ActionId}).")]
    private static partial void LogApproved(ILogger logger, string userId, string tool, string actionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{UserId} cancelled {Tool} (action {ActionId}).")]
    private static partial void LogRejected(ILogger logger, string userId, string tool, string actionId);
}
