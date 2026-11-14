using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Core.Agents;

/// <summary>The policy agent's reply to one turn.</summary>
public sealed record AgentReply(
    string ConversationId,
    int Turn,
    string Answer,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<PendingActionView> PendingActions,
    AgentUsage Usage,
    string? StoppedBecause);

/// <summary>Why a turn could not start.</summary>
public enum AgentTurnRefusal
{
    None,
    NotFound,
    ConversationFull,
    Disabled,
}

public sealed record AgentTurnResult(AgentTurnRefusal Refusal, AgentReply? Reply)
{
    public static AgentTurnResult Refused(AgentTurnRefusal refusal) => new(refusal, null);
}

/// <summary>
/// PriorAuthBot's policy agent (chapter 11): a conversation with the agent from
/// <see cref="PolicyAgentFactory"/>, kept between requests in an <see cref="AgentConversationStore"/>.
/// Each turn is one bounded run. Its tools are chapter 10's, built for the caller; a write the agent
/// proposes becomes chapter 10's pending action and runs only from <c>POST /actions/{id}/approve</c>.
/// </summary>
public sealed class PolicyAgentService(
    PolicyAgentFactory agents,
    AssistantService tools,
    AgentConversationStore conversations)
{
    public async Task<AgentTurnResult> RunAsync(string message, string? conversationId, ToolCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(caller);
        var limits = agents.Limits;
        if (!limits.Enabled)
        {
            return AgentTurnResult.Refused(AgentTurnRefusal.Disabled);
        }

        AgentConversation? existing = null;
        if (conversationId is not null)
        {
            existing = await conversations.GetAsync(caller.UserId, conversationId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return AgentTurnResult.Refused(AgentTurnRefusal.NotFound);
            }

            if (existing.Turns >= limits.MaxTurnsPerConversation)
            {
                return AgentTurnResult.Refused(AgentTurnRefusal.ConversationFull);
            }
        }

        var id = existing?.Id ?? AgentConversationStore.NewId();
        var turn = (existing?.Turns ?? 0) + 1;
        var available = await tools.ToolsForAsync(caller, cancellationToken).ConfigureAwait(false);
        var budget = agents.NewBudget($"{id}/{turn}", caller.UserId);
        var agent = agents.CreatePolicyAgent(available, budget);
        var session = existing is null
            ? await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
            : await agent.DeserializeSessionAsync(existing.Session, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The time limit is the run's own; the request's token still cancels it if the user goes away.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.MaxRunDuration);

        AgentResponse? response = null;
        try
        {
            response = await agent.RunAsync(message, session, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (AgentLimitExceededException)
        {
            // The budget already recorded why. The session is left as it was before this turn.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            budget.StopForTime();
        }

        var contents = response?.Messages.SelectMany(m => m.Contents).ToList() ?? [];
        var proposed = tools.HoldForApproval(contents, available, caller);
        if (response is not null)
        {
            // An approval request left in the history would be sent back to the model next turn as a
            // call with no result. Replace it with a plain note; the approval itself lives in chapter
            // 10's PendingActionStore, bound to this user, and is decided there.
            ReplaceApprovalRequests(session, proposed);
        }

        var answer = budget.StopReason is { } stopped
            ? $"I stopped before finishing: {stopped} Nothing else was done. Try a narrower question."
            : AssistantService.ComposeAnswer(response?.Text, proposed);

        var json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        await conversations.SaveAsync(new AgentConversation(id, caller.UserId, turn, json, default), cancellationToken).ConfigureAwait(false);

        return new AgentTurnResult(AgentTurnRefusal.None, new AgentReply(
            id, turn, answer, AssistantService.Trace(contents), proposed, budget.Usage(), budget.StopReason));
    }

    public Task EndAsync(string conversationId, ToolCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return conversations.DeleteAsync(caller.UserId, conversationId, cancellationToken);
    }

    private static void ReplaceApprovalRequests(AgentSession session, IReadOnlyList<PendingActionView> proposed)
    {
        if (!session.TryGetInMemoryChatHistory(out var history))
        {
            return;
        }

        var changed = false;
        for (var i = 0; i < history.Count; i++)
        {
            if (!history[i].Contents.Any(c => c is ToolApprovalRequestContent))
            {
                continue;
            }

            var kept = history[i].Contents.Where(c => c is not ToolApprovalRequestContent).ToList();
            var names = history[i].Contents.OfType<ToolApprovalRequestContent>().Select(r => (r.ToolCall as FunctionCallContent)?.Name).ToList();
            var note = string.Join(" ", proposed.Where(p => names.Contains(p.Tool)).Select(p => $"(Proposed: {p.Description}. It waits for the user to approve or cancel it; it has not run.)"));
            kept.Add(new TextContent(note.Length > 0 ? note : "(A proposed action was not kept.)"));
            history[i] = new ChatMessage(history[i].Role, kept) { AuthorName = history[i].AuthorName };
            changed = true;
        }

        if (changed)
        {
            session.SetInMemoryChatHistory(history);
        }
    }
}
