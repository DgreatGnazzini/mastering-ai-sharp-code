using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Privacy;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 4: redaction, and the guard for providers that are not approved for PHI.</summary>
public sealed class PhiGuardTests
{
    [Fact]
    public void RedactsIdentifiersByShape()
    {
        var result = PhiRedactor.Redact("Member DD-5820317, DOB 09/22/1978, SSN 123-45-6789, call (555) 201-3344 or jw@example.com, MRN 00412345.");

        Assert.Equal("Member [MEMBER_ID], DOB [DATE], SSN [SSN], call [PHONE] or [EMAIL], [MRN].", result.Text);
        Assert.Equal(6, result.Total);
    }

    [Fact]
    public void RedactsKnownValuesLongestFirst()
    {
        var result = PhiRedactor.Redact("Jordan A. Whitaker called. Jordan wants a status.", ["Jordan", "Jordan A. Whitaker"]);

        Assert.Equal("[NAME] called. [NAME] wants a status.", result.Text);
    }

    [Fact]
    public void LeavesPolicyTextAlone()
    {
        const string text = "Prior authorization requests are reviewed within 72 hours. See policy CLM-201.";

        Assert.Equal(text, PhiRedactor.Redact(text).Text);
    }

    [Fact]
    public async Task GuardRedactsTextBeforeItLeaves()
    {
        var inner = new RecordingChatClient(_ => "ok");
        using var guarded = new PhiGuardChatClient(inner);

        await guarded.GetResponseAsync("Status of DD-5820317?", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Status of [MEMBER_ID]?", inner.Requests.Single().Single().Text);
    }

    [Fact]
    public async Task GuardRefusesImages()
    {
        var inner = new RecordingChatClient(_ => "ok");
        using var guarded = new PhiGuardChatClient(inner);
        ChatMessage message = new(ChatRole.User, [new TextContent("Read this."), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await Assert.ThrowsAsync<PhiNotAllowedException>(() => guarded.GetResponseAsync([message], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(inner.Requests);
    }

    [Theory]
    [InlineData("Ollama", null, true)]
    [InlineData("Offline", null, true)]
    [InlineData("AzureOpenAI", null, false)]
    [InlineData("AzureOpenAI", true, true)]
    [InlineData("OpenAI", null, false)]
    [InlineData("Ollama", false, false)]
    public void HostedProvidersAreNotApprovedUntilSomeoneSaysSo(string provider, bool? approved, bool expected) =>
        Assert.Equal(expected, new AiOptions { Provider = provider, ApprovedForPhi = approved }.IsApprovedForPhi);
}
