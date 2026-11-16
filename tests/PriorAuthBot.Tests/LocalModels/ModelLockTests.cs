using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.LocalModels;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.LocalModels;

/// <summary>Chapter 15: models.lock.json is complete, reviewed, and matches what PriorAuthBot is configured to load.</summary>
public partial class ModelLockTests
{
    private static readonly string[] Kinds = ["Ollama", "OnnxGenAI", "CrossEncoder"];

    private static ModelLock Lock() => ModelLock.Load(Path.Combine(TestPaths.RepoRoot, "models.lock.json"));

    [Fact]
    public void EveryEntryNamesItsSourceLicenseAndReviewer()
    {
        var models = Lock().Models;

        Assert.Equal(models.Count, models.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(models, m =>
        {
            Assert.Contains(m.Kind, Kinds);
            Assert.Matches("^(ollama|huggingface|repository):", m.Source);
            Assert.False(string.IsNullOrWhiteSpace(m.License), $"{m.Id} has no license.");
            Assert.False(string.IsNullOrWhiteSpace(m.LicenseUrl), $"{m.Id} has no license URL.");
            Assert.False(string.IsNullOrWhiteSpace(m.ReviewedBy), $"{m.Id} has no reviewer: someone has to have read the license.");
            Assert.All(m.Files ?? [], f => Assert.True(f.Sha256 is null || Sha256().IsMatch(f.Sha256), $"{m.Id}/{f.Name}: not a SHA-256."));
        });
    }

    [Fact]
    public void EveryModelTheApiIsConfiguredWithIsInTheLock()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestPaths.RepoRoot, "src", "PriorAuthBot.Api", "appsettings.json"))
            .Build()
            .GetSection(AiOptions.SectionName).Get<AiOptions>()!;
        var ids = Lock().Models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(settings.Ollama.ChatModel, ids);
        Assert.Contains(settings.Ollama.EmbeddingModel, ids);
        Assert.Contains(settings.Ollama.VisionModel, ids);
        Assert.Contains(settings.OnnxGenAI.ModelId, ids);
    }

    [Fact]
    public void EveryModelCiPullsIsInTheLock()
    {
        var ids = Lock().Models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var ci = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, ".github", "workflows", "ci.yml"));

        foreach (Match match in ModelEnv().Matches(ci))
        {
            Assert.Contains(match.Groups["model"].Value, ids);
        }
    }

    [Fact]
    public void TheTinyTestModelMatchesItsLockEntry()
    {
        var result = ModelIntegrity.Verify(OnnxGenAIChatClientTests.TinyLock(), OnnxGenAIChatClientTests.TinyModelPath);

        Assert.True(result.Ok, string.Join(" ", result.Problems));
    }

    [Fact]
    public void AResizedFileIsCaughtBeforeItIsHashed()
    {
        var copy = OnnxGenAIChatClientTests.CopyTinyModel();
        File.AppendAllText(Path.Combine(copy, "tokenizer_config.json"), " ");

        var result = ModelIntegrity.Verify(OnnxGenAIChatClientTests.TinyLock(), copy);

        Assert.Contains(result.Problems, p => p.StartsWith("tokenizer_config.json is", StringComparison.Ordinal) && p.Contains("bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFileIsAProblem()
    {
        var copy = OnnxGenAIChatClientTests.CopyTinyModel();
        File.Delete(Path.Combine(copy, "tokenizer.json"));

        Assert.Contains("tokenizer.json is missing.", ModelIntegrity.Verify(OnnxGenAIChatClientTests.TinyLock(), copy).Problems);
    }

    [Fact]
    public void AReadmeNextToTheModelIsFine()
    {
        var copy = OnnxGenAIChatClientTests.CopyTinyModel();
        File.WriteAllText(Path.Combine(copy, "README.md"), "Notes.");

        Assert.True(ModelIntegrity.Verify(OnnxGenAIChatClientTests.TinyLock(), copy).Ok);
    }

    [Fact]
    public void AnOllamaTagWithNewWeightsUnderTheSameNameFails()
    {
        var entry = new LockedModel("llama3.2:3b", "Ollama", "ollama:llama3.2:3b", new string('a', 64), null, "Llama 3.2 Community License", "x", true, "test", []);

        var same = OllamaModelCheck.Check(entry, new Dictionary<string, string> { ["llama3.2:3b"] = new string('a', 64) });
        var repushed = OllamaModelCheck.Check(entry, new Dictionary<string, string> { ["llama3.2:3b"] = new string('b', 64) });
        var missing = OllamaModelCheck.Check(entry, new Dictionary<string, string>());

        Assert.True(same.Ok);
        Assert.Contains(repushed.Problems, p => p.Contains("Someone pulled new weights under the same tag", StringComparison.Ordinal));
        Assert.Contains(missing.Problems, p => p.Contains("not pulled", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnpinnedOllamaModelIsReportedWithTheDigestToReview()
    {
        var entry = new LockedModel("nomic-embed-text:v1.5", "Ollama", "ollama:nomic-embed-text:v1.5", null, null, "Apache-2.0", "x", true, "test", []);

        var result = OllamaModelCheck.Check(entry, new Dictionary<string, string> { ["nomic-embed-text:v1.5"] = new string('c', 64) });

        Assert.Contains(result.Problems, p => p.Contains("no recorded digest", StringComparison.Ordinal) && p.Contains(new string('c', 64), StringComparison.Ordinal));
    }

    [Fact]
    public void ALicenseProblemIsReportedOnlyWhenCommercialUseIsRequired()
    {
        var research = new LockedModel("qwen2.5vl:3b", "Ollama", "ollama:qwen2.5vl:3b", new string('d', 64), null, "Qwen Research License", "x", false, "test", []);
        var installed = new Dictionary<string, string> { ["qwen2.5vl:3b"] = new string('d', 64) };

        Assert.True(OllamaModelCheck.Check(research, installed, requireCommercialUse: false).Ok);
        Assert.False(OllamaModelCheck.Check(research, installed, requireCommercialUse: true).Ok);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    [GeneratedRegex(@"PRIORAUTHBOT_TEST_(CHAT|EMBEDDING|VISION|JUDGE)_MODEL:\s*(?<model>[^\s#]+)")]
    private static partial Regex ModelEnv();
}
