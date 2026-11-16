using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Api;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;
using static PriorAuthBot.Tests.Infrastructure.ScriptedChatClient;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 2's endpoint: streaming, failures as status codes, and the per-user rate limit.</summary>
public sealed class ChatApiTests
{
    [Fact]
    public async Task StreamsServerSentEvents()
    {
        await using var factory = new Factory(new ScriptedChatClient(Reply("You get fifteen days.")));

        var response = await Post(factory, "nurse", "How much PTO do I get?");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("event: delta\ndata: You\n", body, StringComparison.Ordinal);
        Assert.Contains("data:  days.\n", body, StringComparison.Ordinal);
        Assert.EndsWith("event: done\ndata: \n\n", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(429, HttpStatusCode.ServiceUnavailable)]
    [InlineData(503, HttpStatusCode.ServiceUnavailable)]
    [InlineData(408, HttpStatusCode.GatewayTimeout)]
    [InlineData(401, HttpStatusCode.BadGateway)]
    public async Task ProviderFailuresBecomeStatusCodes(int providerStatus, HttpStatusCode expected)
    {
        await using var factory = new Factory(new ScriptedChatClient(Fail(FakePipelineResponse.Exception(providerStatus, "secret provider detail"))));

        var response = await Post(factory, "nurse", "Hello");

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("secret provider detail", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedPromptIs422()
    {
        await using var factory = new Factory(new ScriptedChatClient(Fail(FakePipelineResponse.Exception(400, """{"error":{"code":"content_filter"}}"""))));

        var response = await Post(factory, "nurse", "Hello");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task FilteredAnswerSaysSo()
    {
        await using var factory = new Factory(new ScriptedChatClient(Reply("Here is how to", ChatFinishReason.ContentFilter)));

        var body = await (await Post(factory, "nurse", "Hello")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("event: filtered", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureMidStreamIsAnErrorEvent()
    {
        await using var factory = new Factory(new ScriptedChatClient(FailAfterFirstWord("You get fifteen days.", FakePipelineResponse.Exception(503))));

        var body = await (await Post(factory, "nurse", "Hello")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("data: You\n", body, StringComparison.Ordinal);
        Assert.Contains("event: error", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: done", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachUserHasTheirOwnRateLimit()
    {
        await using var factory = new Factory(new ScriptedChatClient(Reply("ok")), burst: 2);

        var nurse = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            nurse.Add((await Post(factory, "nurse", "Hello")).StatusCode);
        }

        var claims = await Post(factory, "claims", "Hello");

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], nurse);
        Assert.Equal(HttpStatusCode.OK, claims.StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(Factory factory, string user, string message)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return await client.PostAsJsonAsync("/chat", new ChatRequest(message), TestContext.Current.CancellationToken);
    }

    private sealed class Factory(IChatClient model, int burst = 100) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("VectorStore:Provider", "InMemory");
            builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
            builder.UseSetting("RateLimiting:Burst", burst.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("RateLimiting:RequestsPerMinute", "1");
            builder.ConfigureTestServices(services => services.AddSingleton(model));
        }
    }
}
