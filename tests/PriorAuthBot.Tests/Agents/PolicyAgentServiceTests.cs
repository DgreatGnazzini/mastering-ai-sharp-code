using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Agents;
using PriorAuthBot.Core.Tools;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;
using static PriorAuthBot.Tests.Infrastructure.ScriptedToolModel;

namespace PriorAuthBot.Tests.Agents;

/// <summary>
/// The policy agent (chapter 11): a ChatClientAgent over the same pipeline and tools as chapter 10,
/// with a conversation kept between turns, and a budget that stops it.
/// </summary>
public sealed class PolicyAgentServiceTests(MockHrFactory hr) : IClassFixture<MockHrFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolCaller Nurse => new(ToolTestHost.Nurse);

    [Fact]
    public async Task TheAgentPlansToolCallsAsTheUser()
    {
        var model = new ScriptedToolModel(
            Call(PriorAuthBotTools.LookupPolicy, new() { ["policyNumber"] = "HR-104" }),
            Call(PriorAuthBotTools.GetPtoBalance),
            Reply("HR-104 lets you carry over 40 hours, and you have 86.5."));
        await using var host = await AgentTestHost.CreateAsync(hr, model);

        var reply = (await host.Get<PolicyAgentService>().RunAsync("How many of my PTO hours would I lose at year end?", null, Nurse, Ct)).Reply!;

        Assert.Equal(["lookup_policy", "get_pto_balance"], reply.ToolCalls.Select(c => c.Name));
        Assert.All(reply.ToolCalls, c => Assert.Equal("ran", c.Status));
        Assert.Contains("86.5", ToolResults(model.Requests[2]), StringComparison.Ordinal);
        Assert.Equal(3, reply.Usage.ModelCalls);
        Assert.Equal(2, reply.Usage.ToolCalls);
        Assert.Null(reply.StoppedBecause);
    }

    [Fact]
    public async Task ARunawayAgentStopsAtTheStepLimit()
    {
        // A model that never stops asking for the same tool.
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.LookupPolicy, new() { ["policyNumber"] = "HR-104" }));
        await using var host = await AgentTestHost.CreateAsync(hr, model, limits: l => { l.MaxModelCalls = 3; l.MaxToolCalls = 10; });

        var reply = (await host.Get<PolicyAgentService>().RunAsync("Keep checking HR-104.", null, Nurse, Ct)).Reply!;

        Assert.Equal(3, model.Requests.Count);
        Assert.Contains("step limit of 3", reply.StoppedBecause, StringComparison.Ordinal);
        Assert.StartsWith("I stopped before finishing", reply.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolLimitEndsTheLoopWithoutAnotherModelCall()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.GetPtoBalance));
        await using var host = await AgentTestHost.CreateAsync(hr, model, limits: l => { l.MaxModelCalls = 10; l.MaxToolCalls = 2; });

        var reply = (await host.Get<PolicyAgentService>().RunAsync("What's my balance? Check it again. And again.", null, Nurse, Ct)).Reply!;

        Assert.Equal(2, reply.Usage.ToolCalls);
        Assert.Equal(3, model.Requests.Count);
        Assert.Contains("2 tool calls", reply.StoppedBecause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSpendCapStopsTheRun()
    {
        // Each call reports 20,000 input tokens: $0.008 at $0.40 per million. The cap is $0.01.
        var model = new UsageReportingModel(20_000, 100, _ => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c-{Guid.NewGuid():N}", PriorAuthBotTools.GetPtoBalance, new Dictionary<string, object?>())]));
        await using var host = await AgentTestHost.CreateAsync(hr, model, limits: l => { l.MaxModelCalls = 10; l.MaxToolCalls = 10; l.MaxCostUsd = 0.01m; });

        var reply = (await host.Get<PolicyAgentService>().RunAsync("What is my balance?", null, Nurse, Ct)).Reply!;

        Assert.Equal(2, model.Calls);
        Assert.Contains("spend cap", reply.StoppedBecause, StringComparison.Ordinal);
        Assert.True(reply.Usage.Cost <= 0.0165m);
    }

    [Fact]
    public async Task TurnedOffMeansNoRunStarts()
    {
        var model = new ScriptedToolModel(Reply("Hello."));
        await using var host = await AgentTestHost.CreateAsync(hr, model, limits: l => l.Enabled = false);

        var result = await host.Get<PolicyAgentService>().RunAsync("Hello", null, Nurse, Ct);

        Assert.Equal(AgentTurnRefusal.Disabled, result.Refusal);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task TheConversationCarriesOverToTheNextTurn()
    {
        var model = new ScriptedToolModel(
            Call(PriorAuthBotTools.LookupPolicy, new() { ["policyNumber"] = "HR-104" }),
            Reply("HR-104 lets you carry over up to 40 hours."),
            Call(PriorAuthBotTools.GetPtoBalance),
            Reply("You have 86.5 hours, so 46.5 are above the limit."));
        await using var host = await AgentTestHost.CreateAsync(hr, model);
        var agent = host.Get<PolicyAgentService>();

        var first = (await agent.RunAsync("What does HR-104 say about carryover?", null, Nurse, Ct)).Reply!;
        var second = (await agent.RunAsync("And how much of mine is over that?", first.ConversationId, Nurse, Ct)).Reply!;

        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(2, second.Turn);

        // The third request, the first of turn two, holds turn one's question, tool result, and answer.
        var turnTwo = model.Requests[2];
        Assert.Contains(turnTwo, m => m.Role == ChatRole.User && m.Text.Contains("HR-104 say", StringComparison.Ordinal));
        Assert.Contains(turnTwo, m => m.Text.Contains("up to 40 hours", StringComparison.Ordinal));
        Assert.Contains("Paid Time Off Policy", ToolResults(turnTwo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConversationBelongsToOneUser()
    {
        var model = new ScriptedToolModel(Reply("Hello."));
        await using var host = await AgentTestHost.CreateAsync(hr, model);
        var agent = host.Get<PolicyAgentService>();
        var nurses = (await agent.RunAsync("Hello", null, Nurse, Ct)).Reply!;

        var contractor = await agent.RunAsync("What did we talk about?", nurses.ConversationId, new ToolCaller(ToolTestHost.Contractor), Ct);

        Assert.Equal(AgentTurnRefusal.NotFound, contractor.Refusal);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task AConversationHasATurnLimit()
    {
        var model = new ScriptedToolModel(Reply("Hello."));
        await using var host = await AgentTestHost.CreateAsync(hr, model, limits: l => l.MaxTurnsPerConversation = 2);
        var agent = host.Get<PolicyAgentService>();
        var first = (await agent.RunAsync("One", null, Nurse, Ct)).Reply!;
        await agent.RunAsync("Two", first.ConversationId, Nurse, Ct);

        var third = await agent.RunAsync("Three", first.ConversationId, Nurse, Ct);

        Assert.Equal(AgentTurnRefusal.ConversationFull, third.Refusal);
    }

    [Fact]
    public async Task AWriteTheAgentProposesWaitsForChapter10sApproval()
    {
        var ticket = new Dictionary<string, object?> { ["summary"] = "My VPN keeps dropping.", ["category"] = "Network", ["urgency"] = "Normal" };
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.OpenHelpDeskTicket, ticket), Reply("Anything else?"));
        await using var host = await AgentTestHost.CreateAsync(hr, model);
        var agent = host.Get<PolicyAgentService>();

        var reply = (await agent.RunAsync("Open a ticket, my VPN keeps dropping.", null, Nurse, Ct)).Reply!;
        var action = Assert.Single(reply.PendingActions);
        Assert.Equal(0, ((InMemoryHelpDesk)host.Get<IHelpDesk>()).Count);

        // The next turn's history has a note where the approval request was, not a call with no result.
        await agent.RunAsync("Thanks.", reply.ConversationId, Nurse, Ct);
        var history = model.Requests[^1];
        Assert.DoesNotContain(history.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent or FunctionCallContent);
        Assert.Contains(history, m => m.Text.Contains("it has not run", StringComparison.Ordinal));

        // And chapter 10's endpoint runs it, once, for the same user.
        Assert.NotNull(await host.Get<AssistantService>().ApproveAsync(action.Id, Nurse, Ct));
        Assert.Single(host.Get<IHelpDesk>().TicketsFor("demo-nurse"));
    }

    [Fact]
    public async Task TheAgentIsAThinLayerOverTheChatClient()
    {
        var model = new ScriptedToolModel(Reply("Hello."));
        await using var host = await AgentTestHost.CreateAsync(hr, model);
        var factory = host.Get<PolicyAgentFactory>();
        var tools = await host.Get<AssistantService>().ToolsForAsync(Nurse, Ct);

        var agent = factory.CreatePolicyAgent(tools, factory.NewBudget("run", "demo-nurse"));

        Assert.IsType<ChatClientAgent>(agent);
        Assert.NotNull(agent.ChatClient.GetService<BudgetedChatClient>());
        Assert.NotNull(agent.ChatClient.GetService<FunctionInvokingChatClient>());
        Assert.Equal(PolicyAgentFactory.PolicyAgentId, agent.Id);
    }
}
