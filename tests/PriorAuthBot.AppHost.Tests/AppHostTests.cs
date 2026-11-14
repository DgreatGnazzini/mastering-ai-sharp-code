using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace PriorAuthBot.AppHost.Tests;

/// <summary>
/// Chapter 1's AppHost, checked without starting anything: the app model is built, not run, so
/// these tests need no Docker. Running it end to end is a manual step (see docs/chapters/ch01.md).
/// </summary>
public sealed class AppHostTests
{
    [Fact]
    public async Task ModelHasTheContainersAndTheApi()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.PriorAuthBot_AppHost>(TestContext.Current.CancellationToken);

        var names = builder.Resources.Select(r => r.Name).ToHashSet();

        Assert.Superset(new HashSet<string> { "postgres", "priorauthbot", "ollama", "chat", "embedding", "vision", "hr", "api" }, names);
    }

    [Fact]
    public async Task ContainerVersionsArePinned()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.PriorAuthBot_AppHost>(TestContext.Current.CancellationToken);

        var images = builder.Resources.OfType<ContainerResource>()
            .Select(r => r.Annotations.OfType<ContainerImageAnnotation>().Single())
            .ToDictionary(i => i.Image, i => i.Tag);

        Assert.Equal("0.8.7-pg17-trixie", images["pgvector/pgvector"]);
        Assert.Equal("0.40.16", images["ollama/ollama"]);
    }
}
