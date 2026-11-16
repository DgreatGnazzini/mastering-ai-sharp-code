using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Hosting;
using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.LocalModels;

/// <summary>Chapter 15: the audit record's new field, the local-only profile, preview APIs, and the comparison report.</summary>
public partial class UpgradeSafetyTests
{
    [Fact]
    public void ARecordWithNoModelsHashesExactlyAsBefore()
    {
        // Records written before chapter 15 have no Models property. Verification re-serializes each
        // record, so the new property must not appear when it is null, or every old hash would break.
        var record = new AuditRecord { Id = "a1", At = DateTimeOffset.UnixEpoch, UserId = "u", Operation = "POST /ask" };

        var json = JsonSerializer.Serialize(record with { Hash = string.Empty }, AuditChain.Json);

        Assert.DoesNotContain("models", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARecordWithModelsIsHashedWithThem()
    {
        var first = AuditChain.Seal(new AuditRecord { Id = "a1", At = DateTimeOffset.UnixEpoch, UserId = "u", Operation = "POST /ask" }, 1, AuditChain.Genesis);
        var second = AuditChain.Seal(new AuditRecord { Id = "a2", At = DateTimeOffset.UnixEpoch, UserId = "u", Operation = "POST /ask", Models = ["ollama/llama3.2:3b"] }, 2, first.Hash);

        Assert.True(AuditChain.Verify([first, second]).Valid);
        Assert.False(AuditChain.Verify([first, second with { Models = ["openai/gpt-4.1-mini"] }]).Valid);
    }

    [Fact]
    public void TheLocalProfileIsOffUnlessAskedFor()
    {
        Assert.Empty(LocalOnlyProfile.Check(Config(new() { ["AI:Provider"] = "AzureOpenAI" })));
    }

    [Fact]
    public void TheShippedLocalProfileHolds()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestPaths.RepoRoot, "src", "PriorAuthBot.Api", "appsettings.json"))
            .AddJsonFile(Path.Combine(TestPaths.RepoRoot, "src", "PriorAuthBot.Api", "appsettings.LocalOnly.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:priorauthbot"] = "Host=postgres;Database=priorauthbot;Username=priorauthbot_api" })
            .Build();

        Assert.True(LocalOnlyProfile.IsEnabled(configuration));
        Assert.Empty(LocalOnlyProfile.Check(configuration));
    }

    [Fact]
    public void EverySettingThatLeavesTheNetworkIsListed()
    {
        var problems = LocalOnlyProfile.Check(Config(new()
        {
            [LocalOnlyProfile.ProfileKey] = LocalOnlyProfile.LocalOnly,
            ["AI:Provider"] = "AzureOpenAI",
            ["AI:Routing:Enabled"] = "true",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=1",
            ["Security:PromptShield:Provider"] = "ContentSafety",
            ["Audit:Archive:Provider"] = "Blob",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://otlp.example.com:4317",
            ["AI:Ollama:Endpoint"] = "https://ollama.example.com",
            ["ConnectionStrings:priorauthbot"] = "Host=dd.postgres.database.azure.com;Database=p",
        }));

        Assert.Equal(8, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("AI:Provider is AzureOpenAI", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("dd.postgres.database.azure.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.4.0.7", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("192.168.1.10", true)]
    [InlineData("fd00::12", true)]
    [InlineData("ollama", true)]
    [InlineData("models.dd.internal", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("api.openai.com", false)]
    public void PrivateHosts(string host, bool expected)
    {
        Assert.Equal(expected, LocalOnlyProfile.IsPrivateHost(host));
    }

    [Fact]
    public void EveryPreviewApiSuppressionIsInTheInventory()
    {
        // A suppressed experimental diagnostic (OPENAI001, MAAI001, ASPIRE010...) is a promise to come
        // back when the API changes. docs/experimental-apis.md is where that promise is written down.
        var inventory = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "experimental-apis.md"));
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(TestPaths.RepoRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.EndsWith("UpgradeSafetyTests.cs", StringComparison.Ordinal)))
        {
            foreach (Match match in Suppression().Matches(File.ReadAllText(file)))
            {
                foreach (var id in match.Groups["ids"].Value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (PreviewId().IsMatch(id))
                    {
                        found.Add(id);
                    }
                }
            }
        }

        Assert.Contains("OPENAI001", found);
        Assert.All(found, id => Assert.True(inventory.Contains($"`{id}`", StringComparison.Ordinal), $"{id} is suppressed but not listed in docs/experimental-apis.md."));
    }

    [Fact]
    public void TheComparisonMarksARegressionBeyondTheTolerance()
    {
        var current = Summary("llama3.2:3b", relevance: 3.9, contains: 0.79, leaks: 0, p50: 48);
        var candidate = Summary("phi-4-mini-instruct-onnx-cpu-int4", relevance: 3.1, contains: 0.74, leaks: 0, p50: 30);

        var report = ModelComparison.Report(current, candidate, new Tolerances());

        Assert.Contains("| Relevance | 3.90 | 3.10 | -0.80 | WORSE |", report, StringComparison.Ordinal);
        Assert.Contains("| Contains expected fact | 0.79 | 0.74 | -0.05 | same, within tolerance |", report, StringComparison.Ordinal);
        Assert.Contains("| Leaks (must be 0) | 0.00 | 0.00 | +0.00 | ok |", report, StringComparison.Ordinal);
        Assert.Contains("| Latency p50 (s) | 48.00 | 30.00 | -18.00 | measured |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComparisonFailsAnyLeakAndNamesAModelMissingFromTheLock()
    {
        var locks = Core.LocalModels.ModelLock.Load(Path.Combine(TestPaths.RepoRoot, "models.lock.json"));

        var report = ModelComparison.Report(Summary("llama3.2:3b", 3.9, 0.79, 0, 48), Summary("mystery-model", 4.2, 0.9, 1, 20), new Tolerances(), locks);

        Assert.Contains("| Leaks (must be 0) | 0.00 | 1.00 | +1.00 | FAIL |", report, StringComparison.Ordinal);
        Assert.Contains("mystery-model: NOT IN THE LOCK", report, StringComparison.Ordinal);
        Assert.Contains("llama3.2:3b: ollama:llama3.2:3b", report, StringComparison.Ordinal);
    }

    private static EvalSummary Summary(string model, double relevance, double contains, int leaks, double p50) => new(
        "Ollama", model, "qwen2.5:7b", "nomic-embed-text:v1.5", "test", 0.97, 0.97, 0.83, leaks,
        new Dictionary<string, double> { ["Relevance"] = relevance, ["Contains expected fact"] = contains },
        2, 0, null, null, 0, 0, new LatencySummary(8, p50, p50 * 1.5, 1400, 60, 3, 0.0007m), true, []);

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    [GeneratedRegex(@"(#pragma warning disable|<NoWarn>[^<]*?;?)\s*(?<ids>[A-Z][A-Za-z]*\d{3,4}([;, ]+[A-Z][A-Za-z]*\d{3,4})*)")]
    private static partial Regex Suppression();

    /// <summary>Code-analysis and compiler warnings are not preview APIs.</summary>
    [GeneratedRegex(@"^(?!CA|CS|IDE|SYSLIB|NU|xUnit)[A-Z]+\d{3,4}$")]
    private static partial Regex PreviewId();
}
