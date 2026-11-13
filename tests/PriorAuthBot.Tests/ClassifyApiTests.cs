using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Api;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 3's endpoint, end to end through HTTP with a scripted model.</summary>
public sealed class ClassifyApiTests
{
    [Fact]
    public async Task ReturnsIntentAndRoute()
    {
        await using var factory = new Factory("""{"department":"Claims","sensitivity":"Confidential","mentionsPhi":true}""");

        var response = await Client(factory).PostAsJsonAsync("/classify", new ClassifyRequest("Can member 88-1234 appeal?"), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ClassifyResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new QuestionIntent(Department.Claims, Sensitivity.Confidential, true), body!.Intent);
        Assert.Equal(RouteAction.AnswerWithPhiNotice, body.Route.Action);
    }

    [Fact]
    public async Task InvalidModelOutputIsABadGateway()
    {
        await using var factory = new Factory("""{"department":"Finance"}""");

        var response = await Client(factory).PostAsJsonAsync("/classify", new ClassifyRequest("Hello"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("Finance", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static HttpClient Client(Factory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", "nurse");
        return client;
    }

    private sealed class Factory(string reply) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("VectorStore:Provider", "InMemory");
            builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
            builder.ConfigureTestServices(services => services.AddKeyedSingleton<IChatClient>(PriorAuthBot.Core.AI.AiServiceCollectionExtensions.ClassifierKey, new RecordingChatClient(_ => reply)));
        }
    }
}
