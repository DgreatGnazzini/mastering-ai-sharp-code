using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>
/// Chapter 14: the deploy pipeline's smoke test, against the API configured as the Bicep configures
/// it (Production, Entra sign-in, PostgreSQL for the audit log and the ledger). It passes on a good
/// revision and fails on the wrong one. Needs PostgreSQL, like the other pgvector tests.
/// </summary>
[Collection(nameof(PostgresIntegrationTests))]
public sealed class SmokeTestTests
{
    [Fact]
    public async Task AGoodRevisionPassesEveryCheck()
    {
        await using var factory = Production("abc1234");
        var reports = Directory.CreateTempSubdirectory().FullName;

        var exit = await SmokeTest.RunAsync(["--smoke", "http://localhost/", "--expect-revision", "abc1234", "--wait", "60", "--out", reports], factory.Server.CreateHandler());

        Assert.Equal(0, exit);
        Assert.Contains("| Revision | pass |", await File.ReadAllTextAsync(Path.Combine(reports, "eval-smoke.md"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWrongRevisionFails()
    {
        await using var factory = Production("abc1234");

        var exit = await SmokeTest.RunAsync(["--smoke", "http://localhost/", "--expect-revision", "def5678", "--wait", "60", "--out", Directory.CreateTempSubdirectory().FullName], factory.Server.CreateHandler());

        Assert.Equal(1, exit);
    }

    private static WebApplicationFactory<Program> Production(string revision)
    {
        Assert.SkipWhen(LiveSettings.Postgres is null, "Set PRIORAUTHBOT_TEST_POSTGRES (or PRIORAUTHBOT_TEST_TESTCONTAINERS=true) to run against PostgreSQL.");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            foreach (var (key, value) in ProductionSettings.Valid())
            {
                b.UseSetting(key, value);
            }

            // The container in CI signs in with a password from an environment variable; Azure uses Entra.
            b.UseSetting("Postgres:Authentication", "Password");
            b.UseSetting("ConnectionStrings:priorauthbot", LiveSettings.Postgres);
            b.UseSetting("Audit:CreateSchema", "true");
            b.UseSetting("Usage:CreateSchema", "true");
            b.UseSetting("Ingestion:DocumentsPath", TestPaths.SampleDocuments);
            b.UseSetting("PriorAuthBot:Revision", revision);
        });
        _ = factory.Server;
        return factory;
    }
}
