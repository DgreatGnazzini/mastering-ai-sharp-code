using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.Agents;

/// <summary>
/// Chat middleware that charges every model call to an <see cref="AgentBudget"/> (chapter 11). It
/// sits under <see cref="FunctionInvokingChatClient"/>, so it sees each round trip of the tool loop,
/// and over chapter 2's pipeline, so a call it refuses never reaches the PHI guard, the cache, or
/// the provider. When the budget says stop, it throws, and the run ends there.
/// </summary>
public sealed class BudgetedChatClient(IChatClient innerClient, AgentBudget budget) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var estimate = budget.BeforeModelCall(list, options);
        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        budget.AfterModelCall(response, estimate);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Agents in this chapter do not stream. Charging a streamed call means collecting it first.
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }
}
