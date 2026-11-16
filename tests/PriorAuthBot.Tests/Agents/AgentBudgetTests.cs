using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PriorAuthBot.Core.Agents;
using PriorAuthBot.Core.AI;
using Xunit;

namespace PriorAuthBot.Tests.Agents;

/// <summary>The budget on its own (chapter 11): each limit stops the run, and the model has no say.</summary>
public sealed class AgentBudgetTests
{
    private static readonly ChatMessage[] Hello = [new(ChatRole.User, "Hello")];

    private static AgentBudget Budget(AgentOptions limits, Func<bool>? enabled = null) =>
        new("run-1", "demo-nurse", limits, enabled ?? (() => true), new TokenCounter(), NullLogger.Instance);

    [Fact]
    public void TheStepLimitStopsTheNextModelCall()
    {
        var budget = Budget(new AgentOptions { MaxModelCalls = 2 });
        budget.BeforeModelCall(Hello, null);
        budget.BeforeModelCall(Hello, null);

        var stop = Assert.Throws<AgentLimitExceededException>(() => budget.BeforeModelCall(Hello, null));

        Assert.Equal(AgentLimit.StepLimit, stop.Limit);
        Assert.Equal(AgentLimit.StepLimit, budget.StoppedBy);
        Assert.Equal(2, budget.Usage().ModelCalls);
    }

    [Fact]
    public void TheSpendCapCountsWhatTheProviderReports()
    {
        // 10,000 input tokens at $0.40 per million is $0.004 a call, so the third call would pass $0.01.
        var budget = Budget(new AgentOptions { MaxModelCalls = 10, MaxCostUsd = 0.01m });
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = new UsageDetails { InputTokenCount = 10_000, OutputTokenCount = 0 } };
        for (var i = 0; i < 2; i++)
        {
            var estimate = budget.BeforeModelCall(Hello, null);
            budget.AfterModelCall(response, estimate);
        }

        budget.BeforeModelCall(Hello, null);
        budget.AfterModelCall(response, 0);
        var stop = Assert.Throws<AgentLimitExceededException>(() => budget.BeforeModelCall(Hello, null));

        Assert.Equal(AgentLimit.SpendCap, stop.Limit);
        Assert.Equal(0.012m, budget.Usage().Cost);
    }

    [Fact]
    public void WithoutReportedUsageTheBudgetCountsTokensItself()
    {
        var budget = Budget(new AgentOptions());
        var estimate = budget.BeforeModelCall([new(ChatRole.User, "How much PTO do I have left this year?")], null);
        budget.AfterModelCall(new ChatResponse(new ChatMessage(ChatRole.Assistant, "You have 86.5 hours.")), estimate);

        var step = Assert.Single(budget.Usage().Steps);
        Assert.True(step.InputTokens > 5);
        Assert.True(step.OutputTokens > 3);
        Assert.True(step.Cost > 0);
    }

    [Fact]
    public void AnInputThatAloneWouldPassTheCapIsNotSent()
    {
        var budget = Budget(new AgentOptions { MaxCostUsd = 0.000001m });

        var stop = Assert.Throws<AgentLimitExceededException>(() => budget.BeforeModelCall([new(ChatRole.User, string.Join(' ', Enumerable.Repeat("word", 500)))], null));

        Assert.Equal(AgentLimit.SpendCap, stop.Limit);
        Assert.Equal(0, budget.Usage().ModelCalls);
    }

    [Fact]
    public void TheToolLimitRefusesTheNextCall()
    {
        var budget = Budget(new AgentOptions { MaxToolCalls = 1 });

        Assert.True(budget.TryStartToolCall("lookup_policy", out _));
        Assert.False(budget.TryStartToolCall("lookup_policy", out var reason));
        Assert.Contains("1 tool calls", reason, StringComparison.Ordinal);
        Assert.Throws<AgentLimitExceededException>(() => budget.BeforeModelCall(Hello, null));
    }

    [Fact]
    public void TheKillSwitchStopsARunAtItsNextStep()
    {
        var enabled = true;
        var budget = Budget(new AgentOptions(), () => enabled);
        budget.BeforeModelCall(Hello, null);

        enabled = false;

        Assert.Equal(AgentLimit.Disabled, Assert.Throws<AgentLimitExceededException>(() => budget.BeforeModelCall(Hello, null)).Limit);
        Assert.False(budget.TryStartToolCall("get_pto_balance", out _));
    }
}
