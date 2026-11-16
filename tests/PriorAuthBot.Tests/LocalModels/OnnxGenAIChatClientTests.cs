using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.LocalModels;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Usage;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.LocalModels;

/// <summary>
/// Chapter 15: the in-process model, run for real with ONNX Runtime GenAI's native library on every
/// CI platform. The model is the 76 KB one scripts/make-tiny-genai-model.py builds: its tokenizer and
/// chat template are real, and its "weights" are a lookup table that always answers the same sentence.
/// </summary>
public class OnnxGenAIChatClientTests
{
    public const string TinyModelId = "priorauthbot-tiny-test";

    public const string TinyAnswer = "The tiny model answered from source [1].";

    public static string TinyModelPath => Path.Combine(AppContext.BaseDirectory, "LocalModels", "tiny-genai-model");

    internal static LockedModel TinyLock() =>
        ModelLock.Load(Path.Combine(TestPaths.RepoRoot, "models.lock.json")).Get(TinyModelId)
        ?? throw new InvalidOperationException("The tiny test model is missing from models.lock.json.");

    internal static OnnxGenAIOptions TinyOptions(string? path = null) => new() { ModelPath = path ?? TinyModelPath, ModelId = TinyModelId };

    [Fact]
    public async Task AnswersWithTheModelsChatTemplateAndReportsUsage()
    {
        using var client = new OnnxGenAIChatClient(TinyOptions(), TinyLock());

        var response = await client.GetResponseAsync(
            [new(ChatRole.System, "Answer from the sources."), new(ChatRole.User, "How many PTO hours carry over?")],
            new ChatOptions { Temperature = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(TinyAnswer, response.Text);
        Assert.Equal(TinyModelId, response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Usage);
        Assert.True(response.Usage.InputTokenCount > 20, $"Input tokens: {response.Usage.InputTokenCount}");
        Assert.Equal(8, response.Usage.OutputTokenCount);
    }

    [Fact]
    public void BuildsThePromptWithTheTemplateFromTheModelFolder()
    {
        using var client = new OnnxGenAIChatClient(TinyOptions(), TinyLock());

        var prompt = client.BuildPrompt([new(ChatRole.User, "Hello")], new ChatOptions { Instructions = "Be brief." });

        Assert.Equal("<|system|>Be brief.<|end|><|user|>Hello<|end|><|assistant|>", prompt);
    }

    [Fact]
    public async Task StreamsTokenByToken()
    {
        using var client = new OnnxGenAIChatClient(TinyOptions(), TinyLock());
        var pieces = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "Hi")], cancellationToken: TestContext.Current.CancellationToken))
        {
            if (update.Text.Length > 0)
            {
                pieces.Add(update.Text);
            }
        }

        Assert.True(pieces.Count >= 5, $"Got {pieces.Count} pieces.");
        Assert.Equal(TinyAnswer, string.Concat(pieces));
    }

    [Fact]
    public async Task StopsAtMaxOutputTokens()
    {
        using var client = new OnnxGenAIChatClient(TinyOptions(), TinyLock());

        var response = await client.GetResponseAsync([new(ChatRole.User, "Hi")], new ChatOptions { MaxOutputTokens = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal("The tiny model", response.Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    [Fact]
    public void NamesItsModelForCachesAndTheAuditLog()
    {
        using var client = new OnnxGenAIChatClient(TinyOptions(), TinyLock());

        Assert.Equal("onnxruntime-genai/priorauthbot-tiny-test", ModelIdentity.Of(client));
    }

    [Fact]
    public async Task RefusesImages()
    {
        using var client = new OnnxGenAIChatClient(TinyOptions(), TinyLock());
        var message = new ChatMessage(ChatRole.User, [new TextContent("Read this"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync([message], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAChangedWeightFile()
    {
        var copy = CopyTinyModel();
        var bytes = File.ReadAllBytes(Path.Combine(copy, "model.onnx"));
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(Path.Combine(copy, "model.onnx"), bytes);

        var ex = Assert.Throws<ModelIntegrityException>(() => new OnnxGenAIChatClient(TinyOptions(copy), TinyLock()));

        Assert.Contains("model.onnx has SHA-256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnExtraFileTheRuntimeWouldRead()
    {
        // A second config in the folder is how a changed model would sneak in next to unchanged weights.
        var copy = CopyTinyModel();
        File.WriteAllText(Path.Combine(copy, "genai_config.overlay.json"), "{}");

        var ex = Assert.Throws<ModelIntegrityException>(() => new OnnxGenAIChatClient(TinyOptions(copy), TinyLock()));

        Assert.Contains("genai_config.overlay.json is in the model folder but not in the lock", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAModelThatIsNotInTheLock()
    {
        var ex = Assert.Throws<ModelIntegrityException>(() => new OnnxGenAIChatClient(TinyOptions(), lockedModel: null));

        Assert.Contains("not in models.lock.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAModelWithNoRecordedHashes()
    {
        var unpinned = TinyLock() with { Files = [] };

        var ex = Assert.Throws<ModelIntegrityException>(() => new OnnxGenAIChatClient(TinyOptions(), unpinned));

        Assert.Contains("no recorded hashes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAModelWhoseLicenseDoesNotAllowCommercialUse()
    {
        var research = TinyLock() with { CommercialUse = false, License = "Research only" };

        var ex = Assert.Throws<ModelIntegrityException>(() => new OnnxGenAIChatClient(TinyOptions(), research));

        Assert.Contains("not allowing commercial use", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWholeRagPipelineRunsOnTheInProcessModel()
    {
        // Configured exactly as a reader would: AI:Provider=OnnxGenAI and a model path. The embeddings
        // are swapped for the offline ones, because on OnnxGenAI they would come from Ollama.
        await using var host = RagTestHost.Create(
            new Dictionary<string, string?>
            {
                ["AI:Provider"] = "OnnxGenAI",
                ["AI:OnnxGenAI:ModelPath"] = TinyModelPath,
                ["AI:OnnxGenAI:ModelId"] = TinyModelId,
            },
            configure: s => s.AddEmbeddingGenerator(new HashingEmbeddingGenerator()));
        await host.IngestSamplesAsync();
        var nurse = new CallerIdentity("nurse", "Nurse", ["AllStaff", "Clinical"]);

        using var scope = AiRequestContext.Begin("POST /ask", nurse.UserId, nurse.DisplayName);
        var answer = await host.Get<RagAnswerService>().AskAsync("When must a positive sepsis screen be escalated to a physician?", nurse, TestContext.Current.CancellationToken);

        Assert.False(answer.Declined);
        Assert.Equal("The tiny model answered from source [1].", answer.Answer);
        Assert.Single(answer.Citations);
        Assert.Equal(["onnxruntime-genai/priorauthbot-tiny-test"], scope.Context.Models);
        Assert.True(scope.Context.InputTokens > 100);
    }

    [Fact]
    public void EveryClientSharesOneLoadedModel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Provider"] = "OnnxGenAI",
            ["AI:OnnxGenAI:ModelPath"] = TinyModelPath,
            ["AI:OnnxGenAI:ModelId"] = TinyModelId,
        }).Build();
        services.AddPriorAuthBotAI(configuration);
        using var provider = services.BuildServiceProvider();

        var chat = provider.GetRequiredService<IChatClient>().GetService<OnnxGenAIChatClient>();
        var classifier = provider.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ClassifierKey).GetService<OnnxGenAIChatClient>();
        var tools = provider.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.ToolsKey).GetService<OnnxGenAIChatClient>();

        Assert.NotNull(chat);
        Assert.Same(chat, classifier);
        Assert.Same(chat, tools);
        Assert.True(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value.IsApprovedForPhi);
    }

    internal static string CopyTinyModel()
    {
        var copy = Path.Combine(Path.GetTempPath(), "priorauthbot-tiny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(TinyModelPath))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        return copy;
    }
}
