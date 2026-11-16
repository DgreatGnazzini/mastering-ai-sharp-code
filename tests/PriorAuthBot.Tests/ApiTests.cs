using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Api;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// The two-user demo from chapter 7, end to end through HTTP: a nurse and a contractor ask the
/// same questions and get different answers, with demo sign-in and the offline model.
/// </summary>
public sealed class ApiTests(ApiTests.PriorAuthBotFactory factory) : IClassFixture<ApiTests.PriorAuthBotFactory>
{
    [Fact]
    public async Task AskRequiresSignIn()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/ask", new AskRequest("Can I work remotely?"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NurseAndContractorGetDifferentAnswers()
    {
        var nurse = await AskAsync("nurse", "Can I work remotely?");
        var contractor = await AskAsync("contractor", "Can I work remotely?");

        Assert.Equal("remote-work-policy", nurse.Sources[0].DocumentId);
        Assert.Equal("contractor-handbook", contractor.Sources[0].DocumentId);
        Assert.DoesNotContain(contractor.Sources, s => s.DocumentId == "remote-work-policy");
    }

    [Fact]
    public async Task ContractorIsDeclinedForClinicalProtocols()
    {
        var nurse = await AskAsync("nurse", "When must a positive sepsis screen be escalated to a physician?");
        var contractor = await AskAsync("contractor", "When must a positive sepsis screen be escalated to a physician?");

        Assert.False(nurse.Declined);
        Assert.True(contractor.Declined);
        Assert.Equal(RagAnswerService.DeclineMessage, contractor.Answer);
    }

    [Fact]
    public async Task GroupOverageIsForbidden()
    {
        var response = await Client("overage").PostAsJsonAsync("/ask", new AskRequest("Can I work remotely?"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<RagAnswer> AskAsync(string user, string question)
    {
        var response = await Client(user).PostAsJsonAsync("/ask", new AskRequest(question), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RagAnswer>(TestContext.Current.CancellationToken))!;
    }

    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return client;
    }

    public sealed class PriorAuthBotFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        public async ValueTask InitializeAsync() =>
            await Services.GetRequiredService<IngestionWorker>().FirstRunCompleted.WaitAsync(TimeSpan.FromSeconds(60));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("VectorStore:Provider", "InMemory");
            builder.UseSetting("Retrieval:MinScore", GoldenSet.Load().Offline.MinScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
        }
    }
}
