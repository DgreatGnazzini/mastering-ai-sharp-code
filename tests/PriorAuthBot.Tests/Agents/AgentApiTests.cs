using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PriorAuthBot.Api.Agents;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Core.Agents.Exceptions;
using PriorAuthBot.Core.Tools;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Agents;

/// <summary>
/// Chapter 11 end to end through HTTP, with demo sign-in, the offline model, and the mock HR API:
/// the policy agent keeps a conversation per user, and an exception request goes from the nurse to
/// her manager and nobody else.
/// </summary>
public sealed class AgentApiTests(AgentApiTests.Factory factory) : IClassFixture<AgentApiTests.Factory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAgentRequiresSignIn() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/agent", new AgentRequest("Hello"), Ct)).StatusCode);

    [Fact]
    public async Task TheAgentAnswersInAConversationOnlyItsOwnerCanContinue()
    {
        var first = await PostAsync<JsonElement>("nurse", "/agent", new AgentRequest("How much PTO do I have left?"));
        var id = first.GetProperty("conversationId").GetString();

        var second = await Client("nurse").PostAsJsonAsync("/agent", new AgentRequest("Pull up policy HR-104.", id), Ct);
        var other = await Client("claims").PostAsJsonAsync("/agent", new AgentRequest("What did I ask?", id), Ct);

        Assert.Contains("86.5", first.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("turn").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task AnExceptionGoesFromTheNurseToHerManager()
    {
        var started = await PostAsync<ExceptionRunView>("nurse", "/exceptions",
            new ExceptionStartRequest("I want to carry over 60 hours of PTO into next year because I covered the flu surge."));
        Assert.True(started.Status == ExceptionStatus.WaitingForRequester, started.Message);
        Assert.Equal("Grace Lindqvist, RN, nurse manager", started.ApproverName);

        // Before it is sent, the manager cannot see it; nobody else ever can.
        Assert.Equal(HttpStatusCode.NotFound, (await Client("claims").GetAsync($"/exceptions/{started.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client("manager").PostAsJsonAsync($"/exceptions/{started.Id}/decision", new ExceptionDecisionRequest(true), Ct)).StatusCode);

        var sent = await PostAsync<ExceptionRunView>("nurse", $"/exceptions/{started.Id}/submit", new ExceptionSubmitRequest(true));
        var reviews = await (await Client("manager").GetAsync("/exceptions/reviews", Ct)).Content.ReadFromJsonAsync<List<ExceptionRunView>>(Json, Ct);
        var byNurse = await Client("nurse").PostAsJsonAsync($"/exceptions/{started.Id}/decision", new ExceptionDecisionRequest(true), Ct);
        var decided = await PostAsync<ExceptionRunView>("manager", $"/exceptions/{started.Id}/decision", new ExceptionDecisionRequest(true, "Approved."));

        Assert.Equal(ExceptionStatus.WaitingForApprover, sent.Status);
        Assert.Contains(reviews!, r => r.Id == started.Id);
        Assert.Equal(HttpStatusCode.NotFound, byNurse.StatusCode);
        Assert.Equal(ExceptionStatus.Approved, decided.Status);
        Assert.Contains(factory.Services.GetRequiredService<IExceptionRegister>().All, d => d.RunId == started.Id && d.ApproverId == "demo-manager");
    }

    [Fact]
    public async Task AContractorsRequestStopsWithoutReachingAnyone()
    {
        var run = await PostAsync<ExceptionRunView>("contractor", "/exceptions", new ExceptionStartRequest("Carry over 50 hours of PTO for me."));

        Assert.Equal(ExceptionStatus.Stopped, run.Status);
        Assert.Null(run.ApproverName);
    }

    private async Task<T> PostAsync<T>(string user, string path, object body)
    {
        var response = await Client(user).PostAsJsonAsync(path, body, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return client;
    }

    public sealed class Factory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly MockHrFactory _hr = new();

        public async ValueTask InitializeAsync() =>
            await Services.GetRequiredService<IngestionWorker>().FirstRunCompleted.WaitAsync(TimeSpan.FromSeconds(60));

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _hr.DisposeAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("VectorStore:Provider", "InMemory");
            builder.UseSetting("Retrieval:MinScore", GoldenSet.Load().Offline.MinScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(sp => new HrClient(
                    _hr.CreateClient(), sp.GetRequiredService<IDownstreamTokenProvider>(), sp.GetRequiredService<IOptions<HrApiOptions>>())));

                // The workflow's clock before the 1 December deadline, whatever day the tests run. Only the
                // workflow's: the development tokens for the HR API still need the real time.
                services.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<ExceptionRequestService>(sp, (TimeProvider)new ManualTimeProvider())));
            });
        }
    }
}
