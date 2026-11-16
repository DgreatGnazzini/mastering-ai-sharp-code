using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.Agents;

/// <summary>
/// A tool that charges each call to an <see cref="AgentBudget"/> (chapter 11). When the budget
/// refuses, the tool does not run, and it ends the tool loop: <see cref="FunctionInvokingChatClient"/>
/// returns without asking the model for another step.
/// </summary>
public sealed class BudgetedFunction(AIFunction innerFunction, AgentBudget budget) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!budget.TryStartToolCall(Name, out var reason))
        {
            if (FunctionInvokingChatClient.CurrentContext is { } context)
            {
                context.Terminate = true;
            }

            return $"Not run: {reason}";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
