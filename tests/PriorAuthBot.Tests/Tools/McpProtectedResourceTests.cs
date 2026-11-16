using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Tools;

/// <summary>
/// With Entra sign-in (chapter 10), PriorAuthBot publishes OAuth protected resource metadata, so an
/// MCP client that gets a 401 can find the authorization server and the scope to ask the user for.
/// No token is validated here, so no tenant is needed.
/// </summary>
public sealed class McpProtectedResourceTests(McpProtectedResourceTests.EntraFactory factory) : IClassFixture<McpProtectedResourceTests.EntraFactory>
{
    [Fact]
    public async Task TheMetadataNamesTheResourceTheAuthorityAndTheScope()
    {
        var response = await factory.CreateClient().GetAsync("/.well-known/oauth-protected-resource", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("api://priorauthbot", json.RootElement.GetProperty("resource").GetString());
        Assert.Equal(EntraFactory.Authority, json.RootElement.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("api://priorauthbot/PriorAuthBot.Search", json.RootElement.GetProperty("scopes_supported")[0].GetString());
    }

    [Fact]
    public async Task A401PointsAtTheMetadata()
    {
        var response = await factory.CreateClient().PostAsync("/mcp", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    public sealed class EntraFactory : WebApplicationFactory<Program>
    {
        public const string Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Auth:Mode", "Entra");
            builder.UseSetting("Auth:JwtBearer:Authority", Authority);
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("VectorStore:Provider", "InMemory");
            builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
        }
    }
}
