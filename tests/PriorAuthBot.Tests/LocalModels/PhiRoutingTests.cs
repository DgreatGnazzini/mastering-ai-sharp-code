using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Usage;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.LocalModels;

/// <summary>Chapter 15: hybrid routing keeps PHI on the local model.</summary>
public class PhiRoutingTests
{
    private static readonly CallerIdentity Nurse = new("nurse", "Nurse", ["AllStaff", "Clinical"]);
    private static readonly CallerIdentity Compliance = new("compliance", "Dana Whitfield", ["AllStaff", "Compliance"]);

    [Theory]
    [InlineData("How many PTO hours carry over?", ModelRoute.Hosted)]
    [InlineData("Is member DD-4821397 covered for a lumbar MRI?", ModelRoute.Local)]
    [InlineData("Patient MRN 12345678 needs a prior auth.", ModelRoute.Local)]
    [InlineData("Call the member back at 555-201-3344.", ModelRoute.Local)]
    [InlineData("Their date of birth is 03/02/1979.", ModelRoute.Local)]
    public void IdentifiersInTheTextRouteLocal(string question, ModelRoute expected)
    {
        Assert.Equal(expected, PhiRouting.Decide([new(ChatRole.User, question)], null).Route);
    }

    [Fact]
    public void AnImageAlwaysRoutesLocal()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("Read this form."), new DataContent(new byte[] { 1 }, "image/png")]);

        Assert.Equal(new RouteDecision(ModelRoute.Local, "image or file"), PhiRouting.Decide([message], null));
    }

    [Fact]
    public void TheCallersFlagRoutesLocal()
    {
        var options = PhiRouting.Flag(new ChatOptions());

        Assert.Equal(ModelRoute.Local, PhiRouting.Decide([new(ChatRole.User, "What was the outcome?")], options).Route);
    }

    [Fact]
    public void AnIdentifierInARetrievedSourceRoutesLocalToo()
    {
        // The user typed nothing sensitive; a document brought the identifier in.
        var message = new ChatMessage(ChatRole.User, [
            Core.Security.UntrustedContent.Mark("Member DD-4821397 was approved on appeal.", "1"),
            new TextContent("Question: Was the appeal approved?"),
        ]);

        Assert.Equal("identifier: MEMBER_ID", PhiRouting.Decide([message], null).Reason);
    }

    [Fact]
    public async Task RouterSendsEachRequestToOneBranch()
    {
        var hosted = new RecordingChatClient(_ => "hosted");
        var local = new RecordingChatClient(_ => "local");
        using var router = new PhiRoutingChatClient(hosted, local, Microsoft.Extensions.Logging.Abstractions.NullLogger<PhiRoutingChatClient>.Instance);

        var plain = await router.GetResponseAsync("What is the PTO carryover limit?", cancellationToken: TestContext.Current.CancellationToken);
        var phi = await router.GetResponseAsync("What did we decide for member DD-4821397?", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hosted", plain.Text);
        Assert.Equal("local", phi.Text);
        Assert.Single(hosted.Requests);
        Assert.Single(local.Requests);
    }

    [Fact]
    public async Task ALocalFailureIsNeverRetriedOnTheHostedModel()
    {
        var hosted = new RecordingChatClient(_ => "hosted");
        using var router = new PhiRoutingChatClient(hosted, new ThrowingChatClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PhiRoutingChatClient>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => router.GetResponseAsync("Member DD-4821397's appeal?", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(hosted.Requests);
    }

    [Fact]
    public async Task PhiSourcesGoToTheLocalModelAndTheAuditLogSaysWhichModelAnswered()
    {
        // The hosted model is the offline client; the local one is the in-process tiny model, which
        // always answers the same sentence. Which sentence comes back says which model answered.
        await using var host = RoutingHost();
        await host.IngestSamplesAsync();

        using var phiScope = AiRequestContext.Begin("POST /ask", Compliance.UserId, Compliance.DisplayName);
        var phi = await host.Get<RagAnswerService>().AskAsync("What was the outcome of privacy incident 2026-031?", Compliance, TestContext.Current.CancellationToken);
        Assert.Equal(OnnxGenAIChatClientTests.TinyAnswer, phi.Answer);
        Assert.Equal(["onnxruntime-genai/priorauthbot-tiny-test"], phiScope.Context.Models);
    }

    [Fact]
    public async Task QuestionsWithoutPhiGoToTheHostedModel()
    {
        await using var host = RoutingHost();
        await host.IngestSamplesAsync();

        using var scope = AiRequestContext.Begin("POST /ask", Nurse.UserId, Nurse.DisplayName);
        var plain = await host.Get<RagAnswerService>().AskAsync("When must a positive sepsis screen be escalated to a physician?", Nurse, TestContext.Current.CancellationToken);

        Assert.NotEqual(OnnxGenAIChatClientTests.TinyAnswer, plain.Answer);
        Assert.Equal(["offline/offline"], scope.Context.Models);
    }

    [Fact]
    public void OnlyTheHostedBranchHasThePhiGuard()
    {
        // ApprovedForPhi false: the hosted provider has no BAA, so its branch redacts. The local branch
        // is approved by definition and sends text unredacted to a model on Deductible Dynamics' machines.
        using var provider = RoutingServices(new() { ["AI:ApprovedForPhi"] = "false" });

        var hosted = provider.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.HostedRoutePrefix + "chat");
        var local = provider.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.LocalRoutePrefix + "chat");

        Assert.NotNull(hosted.GetService<PhiGuardChatClient>());
        Assert.Null(local.GetService<PhiGuardChatClient>());
        Assert.NotNull(provider.GetRequiredService<IChatClient>().GetService<PhiRoutingChatClient>());
        Assert.NotNull(provider.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ToolsKey).GetService<PhiRoutingChatClient>());
        Assert.NotNull(provider.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey).GetService<PhiRoutingChatClient>());
    }

    [Fact]
    public void ALocalProviderMustBeLocal()
    {
        using var provider = RoutingServices(new() { ["AI:Routing:LocalProvider"] = "AzureOpenAI" });

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChatClient>());

        Assert.Contains("not a local provider", ex.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> RoutingSettings() => new()
    {
        ["AI:Provider"] = "Offline",
        ["AI:Routing:Enabled"] = "true",
        ["AI:Routing:LocalProvider"] = "OnnxGenAI",
        ["AI:OnnxGenAI:ModelPath"] = OnnxGenAIChatClientTests.TinyModelPath,
        ["AI:OnnxGenAI:ModelId"] = OnnxGenAIChatClientTests.TinyModelId,
    };

    private static RagTestHost RoutingHost() => RagTestHost.Create(RoutingSettings());

    private static ServiceProvider RoutingServices(Dictionary<string, string?> overrides)
    {
        var settings = RoutingSettings();
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPriorAuthBotAI(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }
}
