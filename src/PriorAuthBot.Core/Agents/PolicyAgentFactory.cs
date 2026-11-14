using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Core.Agents;

/// <summary>
/// Builds PriorAuthBot's agents (chapter 11). An agent here is a thin layer: a
/// <see cref="ChatClientAgent"/> around the same <see cref="IChatClient"/> pipeline chapter 2 built,
/// with chapter 10's tools, plus two pieces of middleware that make it bounded: a
/// <see cref="FunctionInvokingChatClient"/> with PriorAuthBot's settings, and a
/// <see cref="BudgetedChatClient"/> under it that charges each round trip to the run's budget.
/// Like the tools, an agent is built per run, for one caller and one budget.
/// </summary>
public sealed class PolicyAgentFactory(
    IChatClient chatClient,
    IOptionsMonitor<AgentOptions> options,
    TokenCounter tokens,
    ILoggerFactory loggerFactory,
    TimeProvider? time = null)
{
    public const string PolicyAgentId = "priorauthbot-policy-agent";

    public AgentOptions Limits => options.CurrentValue;

    /// <summary>A new budget for one run, read from the current configuration.</summary>
    public AgentBudget NewBudget(string runId, string userId) =>
        new(runId, userId, options.CurrentValue, () => options.CurrentValue.Enabled, tokens, loggerFactory.CreateLogger<AgentBudget>(), time);

    /// <summary>
    /// The policy agent: chapter 10's tools for this caller, each charged to the budget. A write
    /// still reaches the model as an approval request, so the agent can propose it and never run it.
    /// </summary>
    public ChatClientAgent CreatePolicyAgent(IReadOnlyList<ToolEntry> tools, AgentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(budget);
        List<AITool> scoped = [.. tools.Select(t =>
        {
            var charged = new BudgetedFunction(t.Function, budget);
            return t.IsWrite ? new ApprovalRequiredAIFunction(charged) : (AITool)charged;
        })];

        return new ChatClientAgent(BoundedClient(budget, useTools: true), new ChatClientAgentOptions
        {
            Id = PolicyAgentId,
            Name = "PriorAuthBot",
            Description = "Answers Deductible Dynamics staff questions with PriorAuthBot's tools, as the signed-in user.",

            // The client below already has function invocation with PriorAuthBot's settings. Without
            // this, ChatClientAgent would add its own FunctionInvokingChatClient with the defaults.
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = PromptLibrary.Get(PromptLibrary.AgentPolicyAssistant),
                Tools = scoped,
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = false,
                Temperature = 0,
            },
        }, loggerFactory);
    }

    /// <summary>
    /// An agent with no tools at all, for one model step of a workflow (section 11.4). Tool scoping at
    /// its narrowest: a step that only reads and writes text is given nothing to call.
    /// </summary>
    public ChatClientAgent CreateStepAgent(string id, string instructions, AgentBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(budget);
        return new ChatClientAgent(BoundedClient(budget, useTools: false), new ChatClientAgentOptions
        {
            Id = id,
            Name = id,
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions { Instructions = instructions, Temperature = 0 },
        }, loggerFactory);
    }

    private IChatClient BoundedClient(AgentBudget budget, bool useTools)
    {
        var builder = new ChatClientBuilder(chatClient);
        if (useTools)
        {
            // Outermost: the tool loop. Its own iteration limit sits one above the budget's step
            // limit, so the budget is what stops a long run, with a reason the user can read.
            builder.UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = options.CurrentValue.MaxModelCalls + 1;
                client.MaximumConsecutiveErrorsPerRequest = 2;
                client.AllowConcurrentInvocation = false;
                client.IncludeDetailedErrors = false;
            });
        }

        return builder.Use(inner => new BudgetedChatClient(inner, budget)).Build();
    }
}
