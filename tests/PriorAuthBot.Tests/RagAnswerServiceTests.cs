using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

public class RagAnswerServiceTests
{
    private static readonly CallerIdentity Nurse = new("nurse", "Nurse", ["AllStaff", "Clinical"]);
    private static readonly CallerIdentity Contractor = new("contractor", "Contractor", ["Contractors"]);

    [Fact]
    public async Task ContextSentToTheModelContainsOnlyVisibleDocuments()
    {
        var model = new RecordingChatClient(_ => "Escalate within 15 minutes [1].");
        await using var host = RagTestHost.Create(chatClient: model);
        await host.IngestSamplesAsync();

        var answer = await host.Get<RagAnswerService>().AskAsync("When must a positive sepsis screen be escalated to a physician?", Nurse, TestContext.Current.CancellationToken);

        Assert.False(answer.Declined);
        Assert.Contains(answer.Sources, s => s.DocumentId == "sepsis-screening-protocol");
        var prompt = model.Requests.Single();
        Assert.Equal(ChatRole.System, prompt[0].Role);
        Assert.Contains("<untrusted source=\"1\">", prompt[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("Question: When must a positive sepsis screen be escalated to a physician?", prompt[1].Text, StringComparison.Ordinal);
        Assert.Contains("Sepsis Screening Protocol", prompt[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclinesWithoutCallingTheModelWhenNothingIsVisible()
    {
        await using var host = RagTestHost.Create(chatClient: new ThrowingChatClient());
        await host.IngestSamplesAsync();

        var answer = await host.Get<RagAnswerService>().AskAsync("When must a positive sepsis screen be escalated to a physician?", Contractor, TestContext.Current.CancellationToken);

        Assert.True(answer.Declined);
        Assert.Empty(answer.Sources);
    }

    [Fact]
    public async Task ModelDeclineHidesTheSources()
    {
        var model = new RecordingChatClient(_ => "I don't know based on the documents you can access.");
        await using var host = RagTestHost.Create(chatClient: model);
        await host.IngestSamplesAsync();

        var answer = await host.Get<RagAnswerService>().AskAsync("How many hours of PTO can I carry over into next year?", Nurse, TestContext.Current.CancellationToken);

        Assert.True(answer.Declined);
        Assert.Empty(answer.Sources);
    }
}
