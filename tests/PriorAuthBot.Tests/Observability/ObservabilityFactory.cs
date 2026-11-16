using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Api.Ingestion;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Observability;

/// <summary>The API in Development with demo sign-in, the offline model, and the in-memory audit log, plus any extra settings.</summary>
public class ObservabilityFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected virtual IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>();

    public async ValueTask InitializeAsync() =>
        await Services.GetRequiredService<IngestionWorker>().FirstRunCompleted.WaitAsync(TimeSpan.FromSeconds(60));

    public HttpClient As(string user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("AI:Provider", "Offline");
        builder.UseSetting("VectorStore:Provider", "InMemory");
        builder.UseSetting("Retrieval:MinScore", GoldenSet.Load().Offline.MinScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
        builder.UseSetting("Audit:Store", "InMemory");

        // These tests read the audit log after nearly every request; chapter 2's per-user limit would stop them.
        builder.UseSetting("RateLimiting:Burst", "1000");
        builder.UseSetting("RateLimiting:RequestsPerMinute", "1000");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
