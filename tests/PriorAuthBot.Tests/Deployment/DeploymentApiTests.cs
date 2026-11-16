using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using PriorAuthBot.Api;
using PriorAuthBot.Tests.Observability;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>Chapter 14: the probes Container Apps calls, and the app role every endpoint requires.</summary>
public sealed class DeploymentApiTests(DeploymentApiTests.Factory factory) : IClassFixture<DeploymentApiTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LivenessDependsOnNothing()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", Ct);

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(json.RootElement.GetProperty("checks").EnumerateObject());
    }

    [Fact]
    public async Task ReadinessReportsEachCheckAndTheRevision()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready", Ct);

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("0123abc", json.RootElement.GetProperty("revision").GetString());
        Assert.Equal("Healthy", json.RootElement.GetProperty("checks").GetProperty("ingestion").GetString());
        Assert.Equal("Healthy", json.RootElement.GetProperty("checks").GetProperty("postgres").GetString());
    }

    [Fact]
    public async Task ASignedInUserWithoutTheAppRoleIsRefused()
    {
        var response = await factory.As("former").PostAsJsonAsync("/ask", new AskRequest("Can I work remotely?"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AUserWithTheAppRoleGetsAnAnswer()
    {
        var response = await factory.As("nurse").PostAsJsonAsync("/ask", new AskRequest("Can I work remotely?"), Ct);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TheRoleAlsoGuardsTheMcpEndpoint()
    {
        var response = await factory.As("former").PostAsync("/mcp", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public sealed class Factory : ObservabilityFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PriorAuthBot:Revision", "0123abc");
        }
    }
}
