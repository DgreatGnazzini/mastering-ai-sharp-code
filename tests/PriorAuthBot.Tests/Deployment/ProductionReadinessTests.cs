using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PriorAuthBot.Core.Hosting;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>
/// Chapter 14: the startup checks. The configuration the Bicep deploys passes; each setting that is
/// fine on a laptop and a defect in Azure fails, with a message that names it.
/// </summary>
public sealed class ProductionReadinessTests
{
    [Fact]
    public void TheDeployedConfigurationIsReady()
    {
        Assert.Empty(ProductionReadiness.Check(Build(ProductionSettings.Valid()), "Production"));
    }

    [Fact]
    public void DevelopmentIsNeverChecked()
    {
        var settings = ProductionSettings.Valid();
        settings["Auth:Mode"] = "Demo";
        settings["Usage:Ledger"] = "InMemory";

        Assert.Empty(ProductionReadiness.Check(Build(settings), "Development"));
    }

    [Theory]
    [InlineData("Security:Guardrails", "false", "Security:Guardrails")]
    [InlineData("Telemetry:CaptureMessageContent", "true", "Telemetry:CaptureMessageContent")]
    [InlineData("Logging:LogLevel:Microsoft.Extensions.AI", "Trace", "Microsoft.Extensions.AI")]
    [InlineData("Audit:Store", "InMemory", "Audit:Store")]
    [InlineData("Auth:Mode", "Demo", "Auth:Mode")]
    [InlineData("Auth:RequiredRole", "", "Auth:RequiredRole")]
    [InlineData("Auth:JwtBearer:Audience", "", "Audience")]
    [InlineData("Usage:Ledger", "InMemory", "Usage:Ledger")]
    [InlineData("Azure:ManagedIdentityClientId", "", "Azure:ManagedIdentityClientId")]
    [InlineData("Auth:JwtBearer:Authority", "https://login.microsoftonline.com/<tenant-id>/v2.0", "Auth:JwtBearer:Authority")]
    [InlineData("Tools:Downstream:ClientId", "<priorauthbot-app-registration-client-id>", "Tools:Downstream:ClientId")]
    public void EachLaptopSettingIsRefused(string key, string value, string named)
    {
        var settings = ProductionSettings.Valid();
        settings[key] = value;

        var problems = ProductionReadiness.Check(Build(settings), "Production");

        Assert.Contains(problems, p => p.Contains(named, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        var settings = ProductionSettings.Valid();
        settings["Auth:Mode"] = "Demo";
        settings["Usage:Ledger"] = "InMemory";
        settings["Audit:Store"] = "InMemory";

        var ex = Assert.Throws<InvalidOperationException>(() => ProductionReadiness.EnsureReady(Build(settings), "Staging"));

        Assert.Contains("Auth:Mode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Usage:Ledger", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Audit:Store", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretInAFileIsRefusedButTheSameKeyFromTheEnvironmentIsNot()
    {
        var file = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "AI": { "OpenAI": { "ApiKey": "sk-in-the-image" } }, "ConnectionStrings": { "qdrant": "Host=q;Password=hunter2" } }""");
        try
        {
            var fromFile = new ConfigurationBuilder().AddInMemoryCollection(ProductionSettings.Valid()).AddJsonFile(file).Build();
            var problems = ProductionReadiness.Check(fromFile, "Production");
            Assert.Contains(problems, p => p.Contains("AI:OpenAI:ApiKey", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.Contains("ConnectionStrings:qdrant", StringComparison.Ordinal));

            // An environment variable (a Container Apps secret, backed by Key Vault) added after the file wins.
            var overridden = new ConfigurationBuilder()
                .AddInMemoryCollection(ProductionSettings.Valid())
                .AddJsonFile(file)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:OpenAI:ApiKey"] = "from-key-vault", ["ConnectionStrings:qdrant"] = "Host=q;Password=from-key-vault" })
                .Build();
            Assert.Empty(ProductionReadiness.Check(overridden, "Production"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheShippedAppSettingsHoldNoSecrets()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Infrastructure.TestPaths.RepoRoot, "src", "PriorAuthBot.Api", "appsettings.json"))
            .Build();

        Assert.Empty(ProductionReadiness.SecretsInFiles(configuration));
    }

    [Fact]
    public void TheApiDoesNotStartInProductionWithTheTemplateSettings()
    {
        using var production = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("AI:Provider", "Offline");
            b.UseSetting("VectorStore:Provider", "InMemory");
        });

        var ex = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("<tenant-id>", ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:Ledger", ex.ToString(), StringComparison.Ordinal);
    }

    private static IConfigurationRoot Build(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
