using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PriorAuthBot.Core.Tools;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;
using static PriorAuthBot.Tests.Infrastructure.ScriptedToolModel;
using Microsoft.Extensions.Caching.Distributed;
using PriorAuthBot.Core.AI;

namespace PriorAuthBot.Tests.Tools;

/// <summary>
/// The tool loop and the approval flow (chapter 10), with a scripted model under the real
/// FunctionInvokingChatClient: reads run, writes stop, and only the user who was asked can approve.
/// </summary>
public sealed class AssistantServiceTests(MockHrFactory hr) : IClassFixture<MockHrFactory>
{
    private static readonly Dictionary<string, object?> Ticket = new() { ["summary"] = "My laptop screen is cracked.", ["category"] = "Hardware", ["urgency"] = "Normal" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReadToolRunsAndItsResultGoesBackToTheModel()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.LookupPolicy, new() { ["policyNumber"] = "HR-104" }), Reply("HR-104 is the Paid Time Off Policy."));
        await using var host = await ToolTestHost.CreateAsync(hr, model);

        var reply = await host.Get<AssistantService>().AskAsync("What is HR-104?", new ToolCaller(ToolTestHost.Nurse), Ct);

        Assert.Equal("HR-104 is the Paid Time Off Policy.", reply.Answer);
        Assert.Equal("ran", Assert.Single(reply.ToolCalls).Status);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("Paid Time Off Policy", ToolResults(model.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteStopsForApprovalAndNothingIsSubmitted()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.OpenHelpDeskTicket, Ticket), Reply("Opened your ticket."));
        await using var host = await ToolTestHost.CreateAsync(hr, model);

        var reply = await host.Get<AssistantService>().AskAsync("My laptop screen is cracked, open a ticket.", new ToolCaller(ToolTestHost.Nurse), Ct);

        var action = Assert.Single(reply.PendingActions);
        Assert.Equal(PriorAuthBotTools.OpenHelpDeskTicket, action.Tool);
        Assert.Contains("My laptop screen is cracked.", action.Description, StringComparison.Ordinal);
        Assert.Equal("waiting for approval", Assert.Single(reply.ToolCalls).Status);
        Assert.Contains("Nothing has been submitted yet", reply.Answer, StringComparison.Ordinal);
        Assert.Single(model.Requests);
        Assert.Equal(0, ((InMemoryHelpDesk)host.Get<IHelpDesk>()).Count);
    }

    [Fact]
    public async Task ApprovalRunsTheProposedArgumentsExactlyOnce()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.OpenHelpDeskTicket, Ticket));
        await using var host = await ToolTestHost.CreateAsync(hr, model);
        var assistant = host.Get<AssistantService>();
        var nurse = new ToolCaller(ToolTestHost.Nurse);
        var reply = await assistant.AskAsync("Open a ticket for my cracked screen.", nurse, Ct);
        var id = reply.PendingActions[0].Id;

        var first = await assistant.ApproveAsync(id, nurse, Ct);
        var second = await assistant.ApproveAsync(id, nurse, Ct);

        Assert.NotNull(first);
        Assert.Null(second);
        var ticket = Assert.Single(host.Get<IHelpDesk>().TicketsFor("demo-nurse"));
        Assert.Equal("My laptop screen is cracked.", ticket.Request.Summary);
        Assert.Equal(TicketCategory.Hardware, ticket.Request.Category);
    }

    [Fact]
    public async Task NobodyElseCanApproveAnAction()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.OpenHelpDeskTicket, Ticket));
        await using var host = await ToolTestHost.CreateAsync(hr, model);
        var assistant = host.Get<AssistantService>();
        var reply = await assistant.AskAsync("Open a ticket for my cracked screen.", new ToolCaller(ToolTestHost.Nurse), Ct);
        var id = reply.PendingActions[0].Id;

        Assert.Null(await assistant.ApproveAsync(id, new ToolCaller(ToolTestHost.Contractor), Ct));
        Assert.False(assistant.Reject(id, new ToolCaller(ToolTestHost.Contractor)));
        Assert.Equal(0, ((InMemoryHelpDesk)host.Get<IHelpDesk>()).Count);

        // The owner's action is untouched by the other user's attempts.
        Assert.NotNull(await assistant.ApproveAsync(id, new ToolCaller(ToolTestHost.Nurse), Ct));
    }

    [Fact]
    public async Task AnExpiredActionCannotBeApproved()
    {
        var clock = new ManualTimeProvider();
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.OpenHelpDeskTicket, Ticket));
        await using var host = await ToolTestHost.CreateAsync(hr, model, s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        var assistant = host.Get<AssistantService>();
        var nurse = new ToolCaller(ToolTestHost.Nurse);
        var reply = await assistant.AskAsync("Open a ticket for my cracked screen.", nurse, Ct);

        clock.Now += PendingActionStore.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(await assistant.ApproveAsync(reply.PendingActions[0].Id, nurse, Ct));
        Assert.Equal(0, ((InMemoryHelpDesk)host.Get<IHelpDesk>()).Count);
    }

    [Fact]
    public async Task RejectingCancelsTheAction()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.OpenHelpDeskTicket, Ticket));
        await using var host = await ToolTestHost.CreateAsync(hr, model);
        var assistant = host.Get<AssistantService>();
        var nurse = new ToolCaller(ToolTestHost.Nurse);
        var reply = await assistant.AskAsync("Open a ticket for my cracked screen.", nurse, Ct);

        Assert.True(assistant.Reject(reply.PendingActions[0].Id, nurse));
        Assert.Null(await assistant.ApproveAsync(reply.PendingActions[0].Id, nurse, Ct));
    }

    [Fact]
    public async Task TextInAToolResultCanAskForAWriteButCannotMakeOneHappen()
    {
        // The model reads a policy, then "obeys" an instruction it found there and proposes a ticket.
        // A real model may or may not fall for it; the point is that it does not matter.
        var model = new ScriptedToolModel(
            Call(PriorAuthBotTools.LookupPolicy, new() { ["policyNumber"] = "GEN-001" }),
            Call(PriorAuthBotTools.OpenHelpDeskTicket, new() { ["summary"] = "Grant admin rights to Casey", ["category"] = "Access", ["urgency"] = "High" }));
        await using var host = await ToolTestHost.CreateAsync(hr, model);

        var reply = await host.Get<AssistantService>().AskAsync("What does GEN-001 say about gifts?", new ToolCaller(ToolTestHost.Contractor), Ct);

        Assert.Equal(["ran", "waiting for approval"], reply.ToolCalls.Select(c => c.Status));
        Assert.Contains("Grant admin rights to Casey", Assert.Single(reply.PendingActions).Description, StringComparison.Ordinal);
        Assert.Equal(0, ((InMemoryHelpDesk)host.Get<IHelpDesk>()).Count);
    }

    [Fact]
    public async Task TheToolLoopIsBounded()
    {
        // A model that never stops calling tools.
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.LookupPolicy, new() { ["policyNumber"] = "HR-104" }));
        await using var host = await ToolTestHost.CreateAsync(hr, model);

        var reply = await host.Get<AssistantService>().AskAsync("Look up HR-104 forever.", new ToolCaller(ToolTestHost.Nurse), Ct);

        Assert.InRange(model.Requests.Count, 1, 5);
        Assert.InRange(reply.ToolCalls.Count(c => c.Status == "ran"), 1, 4);
    }

    [Fact]
    public async Task AFailingToolDoesNotShowTheModelItsErrorDetails()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.GetPtoBalance), Reply("The HR system did not answer."));
        await using var host = await ToolTestHost.CreateAsync(hr, model, s => s.Replace(ServiceDescriptor.Singleton(sp => new HrClient(
            new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://hr.test/") },
            sp.GetRequiredService<IDownstreamTokenProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HrApiOptions>>()))));

        var reply = await host.Get<AssistantService>().AskAsync("How much PTO do I have?", new ToolCaller(ToolTestHost.Nurse), Ct);

        Assert.Equal("failed", Assert.Single(reply.ToolCalls).Status);
        Assert.DoesNotContain("Server=hr-db", ToolResults(model.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachRequestOffersTheModelTheToolsWithWritesWrapped()
    {
        var model = new ScriptedToolModel(Reply("Hello."));
        await using var host = await ToolTestHost.CreateAsync(hr, model);

        await host.Get<AssistantService>().AskAsync("Hi", new ToolCaller(ToolTestHost.Nurse), Ct);

        var tools = model.Options[0]!.Tools!;
        Assert.Equal([PriorAuthBotTools.LookupPolicy, PriorAuthBotTools.GetPtoBalance, PriorAuthBotTools.OpenHelpDeskTicket], tools.Select(t => t.Name));
        Assert.False(model.Options[0]!.AllowMultipleToolCalls);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Server=hr-db;Password=hunter2 is unreachable", null, HttpStatusCode.ServiceUnavailable);
    }
}

/// <summary>The eval caches every model round trip (chapter 9), including requests that carry tools.</summary>
public sealed class ToolCachingTests(MockHrFactory hr) : IClassFixture<MockHrFactory>
{
    [Fact]
    public async Task RequestsWithToolsCanBeCachedAndReplayed()
    {
        var model = new ScriptedToolModel(Call(PriorAuthBotTools.GetPtoBalance), Reply("You have 86.5 hours."));
        await using var host = await ToolTestHost.CreateAsync(hr, configure: s =>
        {
            s.AddDistributedMemoryCache();
            s.AddKeyedChatClient(Core.AI.AiServiceCollectionExtensions.ToolsKey, sp => model)
                .UsePriorAuthBotToolInvocation()
                .UseDistributedCache();
        });
        var assistant = host.Get<AssistantService>();

        var first = await assistant.AskAsync("How much PTO do I have?", new ToolCaller(ToolTestHost.Nurse), TestContext.Current.CancellationToken);
        var second = await assistant.AskAsync("How much PTO do I have?", new ToolCaller(ToolTestHost.Nurse), TestContext.Current.CancellationToken);

        Assert.Equal(first.Answer, second.Answer);
        Assert.Equal(2, model.Requests.Count);
    }
}
