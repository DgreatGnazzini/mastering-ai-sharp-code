using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PriorAuthBot.Api;
using PriorAuthBot.Core.Search;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 5's endpoint, end to end with the offline embedder.</summary>
public sealed class CatalogApiTests
{
    [Fact]
    public async Task AnyEmployeeCanSearchTheCatalog()
    {
        await using var factory = new Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", "contractor");

        var response = await client.PostAsJsonAsync("/catalog/search", new CatalogSearchRequest("lactation rooms for nursing parents", 3), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var hits = await response.Content.ReadFromJsonAsync<List<CatalogHit>>(TestContext.Current.CancellationToken);
        Assert.Equal(3, hits!.Count);
        Assert.Equal("Lactation Accommodation", hits[0].Title);
    }

    [Fact]
    public async Task EmptyQueriesAreRejected()
    {
        await using var factory = new Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", "nurse");

        var response = await client.PostAsJsonAsync("/catalog/search", new CatalogSearchRequest(" "), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignInIsRequired()
    {
        await using var factory = new Factory();

        var response = await factory.CreateClient().PostAsJsonAsync("/catalog/search", new CatalogSearchRequest("PTO"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AI:Provider", "Offline");
            builder.UseSetting("Catalog:Path", TestPaths.PolicyCatalog);
        }
    }
}
