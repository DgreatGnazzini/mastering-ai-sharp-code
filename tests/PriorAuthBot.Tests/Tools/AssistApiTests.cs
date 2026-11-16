using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Api.Tools;
using PriorAuthBot.Core.Mcp;
using PriorAuthBot.Core.Tools;
using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Tools;

/// <summary>
/// Chapter 10 end to end through HTTP, with demo sign-in, the offline model, the mock HR API, and
/// the formulary MCP server: tools run as the signed-in user, and a write needs that user's approval.
/// PriorAuthBot's own MCP endpoint is tested here too, because it shares the API's sign-in.
/// </summary>
public sealed class AssistApiTests(AssistApiTests.Factory factory) : IClassFixture<AssistApiTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AssistRequiresSignIn() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/assist", new AssistRequest("How much PTO do I have left?"), Ct)).StatusCode);

    [Fact]
    public async Task EachUserGetsTheirOwnPtoBalance()
    {
        var nurse = await AssistAsync("nurse", "How much PTO do I have left?");
        var hr = await AssistAsync("hr", "How much PTO do I have left?");

        Assert.Contains("86.5", nurse.Answer, StringComparison.Ordinal);
        Assert.Contains("120.25", hr.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("120.25", nurse.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATicketNeedsTheSameUsersApproval()
    {
        var reply = await AssistAsync("nurse", "My laptop screen is broken, please open a help desk ticket.");
        var action = Assert.Single(reply.PendingActions);

        var byContractor = await Client("contractor").PostAsync($"/actions/{action.Id}/approve", null, Ct);
        var byNurse = await Client("nurse").PostAsync($"/actions/{action.Id}/approve", null, Ct);
        var again = await Client("nurse").PostAsync($"/actions/{action.Id}/approve", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, byContractor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byNurse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        var outcome = await byNurse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.StartsWith("INC-", outcome.GetProperty("result").GetProperty("ticketNumber").GetString(), StringComparison.Ordinal);
        Assert.Single(factory.Services.GetRequiredService<IHelpDesk>().TicketsFor("demo-nurse"));
    }

    [Fact]
    public async Task UnknownActionsAreNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client("nurse").PostAsync("/actions/0123456789abcdef/approve", null, Ct)).StatusCode);

    [Fact]
    public async Task VettedMcpToolsAreAvailable()
    {
        var reply = await AssistAsync("claims", "Is Ozempic covered on the formulary?");

        Assert.Equal("formulary_search_formulary", Assert.Single(reply.ToolCalls).Name);
        Assert.Contains("semaglutide", reply.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMcpEndpointRequiresSignIn() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }), Ct)).StatusCode);

    [Fact]
    public async Task TheMcpEndpointSearchesAsTheSignedInUser()
    {
        await using var nurse = await McpClientAsync("nurse");
        await using var contractor = await McpClientAsync("contractor");

        var tool = Assert.Single(await nurse.ListToolsAsync(cancellationToken: Ct));
        Assert.Equal("search_policies", tool.Name);
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);

        var arguments = new Dictionary<string, object?> { ["query"] = "When must a positive sepsis screen be escalated?" };
        var forNurse = (await nurse.CallToolAsync("search_policies", arguments, cancellationToken: Ct)).Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        var forContractor = (await contractor.CallToolAsync("search_policies", arguments, cancellationToken: Ct)).Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;

        Assert.Contains("sepsis-screening-protocol", forNurse, StringComparison.Ordinal);
        Assert.DoesNotContain("sepsis-screening-protocol", forContractor, StringComparison.Ordinal);
        Assert.DoesNotContain("lactate", forContractor, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AssistantReply> AssistAsync(string user, string message)
    {
        var response = await Client(user).PostAsJsonAsync("/assist", new AssistRequest(message), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AssistantReply>(Ct))!;
    }

    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return client;
    }

    private async Task<McpClient> McpClientAsync(string user) =>
        await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(factory.Server.BaseAddress, "/mcp"), AdditionalHeaders = new Dictionary<string, string> { ["X-Demo-User"] = user } },
            factory.CreateClient(), NullLoggerFactory.Instance, ownsHttpClient: true), cancellationToken: Ct);

    public sealed class Factory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly MockHrFactory _hr = new();
        private readonly List<IAsyncDisposable> _servers = [];

        public async ValueTask InitializeAsync() =>
            await Services.GetRequiredService<IngestionWorker>().FirstRunCompleted.WaitAsync(TimeSpan.FromSeconds(60));

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            foreach (var server in _servers)
            {
                await server.DisposeAsync();
            }

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
                // The HR API in this process, reached the way the API reaches it, through the development token provider.
                services.Replace(ServiceDescriptor.Singleton(sp => new HrClient(
                    _hr.CreateClient(), sp.GetRequiredService<IDownstreamTokenProvider>(), sp.GetRequiredService<IOptions<HrApiOptions>>())));

                // The formulary server in this process, vetted against the API's own pins.
                services.Replace(ServiceDescriptor.Singleton(_ => new McpToolProvider(
                    Options.Create(ToolEvaluation.ApiMcpOptions()), NullLoggerFactory.Instance, async (_, _, ct) =>
                    {
                        var (server, client) = await InProcessMcpServer.StartAsync(InProcessMcpServer.FormularyTools(), cancellationToken: ct);
                        _servers.Add(server);
                        return client;
                    })));
            });
        }
    }
}
