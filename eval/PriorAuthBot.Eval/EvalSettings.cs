using System.Globalization;

namespace PriorAuthBot.Eval;

/// <summary>
/// Command line and environment. The environment variables are the ones the test project's live
/// tests read, so the CI job sets them once for both.
/// </summary>
internal sealed record EvalSettings(
    string Provider,
    bool Answers,
    bool Gate,
    string ReportDirectory,
    string CacheDirectory,
    string BaselinesPath,
    string ExecutionName,
    bool Tools = false,
    bool Agents = false,
    bool RedTeam = false,
    bool Ops = false,
    string? BaselineProvider = null,
    int LatencySamples = 0)
{
    public const string Offline = "Offline";
    public const string Ollama = "Ollama";

    /// <summary>
    /// Chapter 14: Azure OpenAI, for the eval job that runs inside the deployment's virtual network
    /// against the model deployment a new revision will use. Signed in with the job's managed identity.
    /// </summary>
    public const string AzureOpenAI = "AzureOpenAI";

    /// <summary>
    /// Chapter 15: a model running in the eval's own process with ONNX Runtime GenAI. Embeddings and the
    /// judge stay on Ollama, so only the answering model differs from an Ollama run.
    /// </summary>
    public const string OnnxGenAI = "OnnxGenAI";

    public static string? OnnxModelPath => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_ONNX_MODEL");

    public static string OnnxModelId => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_ONNX_MODEL_ID") ?? new Core.AI.OnnxGenAIOptions().ModelId;

    /// <summary>
    /// Chapter 15: whose baselines the gate compares with. A candidate model is held to the bar the
    /// current model set (<c>--baseline-provider Ollama</c>), not to a bar of its own.
    /// </summary>
    public string GateProvider => BaselineProvider ?? Provider;

    /// <summary>A real model answers, so calls are slow, cached on disk, and judged.</summary>
    public bool IsLive => Provider != Offline;

    public static string? AzureEndpoint => Environment.GetEnvironmentVariable("PRIORAUTHBOT_EVAL_AZURE_OPENAI_ENDPOINT");

    public static string AzureChatDeployment => Environment.GetEnvironmentVariable("PRIORAUTHBOT_EVAL_CHAT_DEPLOYMENT") ?? "gpt-4.1-mini";

    public static string AzureEmbeddingDeployment => Environment.GetEnvironmentVariable("PRIORAUTHBOT_EVAL_EMBEDDING_DEPLOYMENT") ?? "text-embedding-3-small";

    public static string AzureJudgeDeployment => Environment.GetEnvironmentVariable("PRIORAUTHBOT_EVAL_JUDGE_DEPLOYMENT") ?? AzureChatDeployment;

    /// <summary>The minimum similarity for this embedding model. text-embedding-3-small scores lower than nomic-embed-text; calibrate it on the first run.</summary>
    public static string? AzureMinScore => Environment.GetEnvironmentVariable("PRIORAUTHBOT_EVAL_MIN_SCORE");

    /// <summary>The model that answers, as the reports name it.</summary>
    public string AnsweringModel => Provider switch
    {
        Ollama => ChatModel,
        OnnxGenAI => OnnxModelId,
        AzureOpenAI => AzureChatDeployment,
        _ => "offline",
    };

    /// <summary>The model that judges answers, as the reports name it.</summary>
    public string JudgingModel => Provider == AzureOpenAI ? AzureJudgeDeployment : JudgeModel;

    /// <summary>The LLM judge's chat client, for a live provider.</summary>
    public Microsoft.Extensions.AI.IChatClient CreateJudge() => Provider switch
    {
        Ollama or OnnxGenAI => new OllamaSharp.OllamaApiClient(
            new HttpClient { BaseAddress = new Uri(OllamaEndpoint!), Timeout = TimeSpan.FromMinutes(10) }, JudgeModel),
        AzureOpenAI => Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(Core.AI.AiServiceCollectionExtensions
            .CreateAzureOpenAI(new Core.AI.AzureOpenAIOptions { Endpoint = new Uri(AzureEndpoint!) }, Core.Hosting.AzureIdentity.CreateCredential(AzureIdentityFromEnvironment()))
            .GetChatClient(AzureJudgeDeployment)),
        _ => throw new InvalidOperationException("The offline provider has no judge."),
    };

    private static Core.Hosting.AzureIdentityOptions AzureIdentityFromEnvironment() => new()
    {
        ManagedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"),
        TenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID"),
    };

    public static string? OllamaEndpoint => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_OLLAMA");

    public static string ChatModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_CHAT_MODEL") ?? "llama3.2:3b";

    public static string EmbeddingModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_EMBEDDING_MODEL") ?? "nomic-embed-text:v1.5";

    public static string JudgeModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_JUDGE_MODEL") ?? ChatModel;

    public static string? RerankerModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_RERANKER");

    public static EvalSettings Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var provider = Value("--provider") ?? Offline;
        if (provider is not (Offline or Ollama or OnnxGenAI or AzureOpenAI))
        {
            throw new ArgumentException($"--provider must be {Offline}, {Ollama}, {OnnxGenAI}, or {AzureOpenAI}, not '{provider}'.");
        }

        return new EvalSettings(
            provider,
            args.Contains("--answers"),
            !args.Contains("--no-gate"),
            Value("--out") ?? Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_REPORTS") ?? Path.Combine(Directory.GetCurrentDirectory(), "eval-reports"),
            Value("--cache") ?? Environment.GetEnvironmentVariable("PRIORAUTHBOT_EVAL_CACHE") ?? Path.Combine(EvalPaths.RepoRoot, ".eval-cache"),
            Value("--baselines") ?? EvalPaths.Baselines,
            Value("--execution") ?? DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
            args.Contains("--tools"),
            args.Contains("--agents"),
            args.Contains("--redteam"),
            args.Contains("--ops"),
            Value("--baseline-provider"),
            args.Contains("--latency") ? int.Parse(Value("--latency") is { } n && !n.StartsWith("--", StringComparison.Ordinal) ? n : "8", CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>PriorAuthBot's configuration for this provider, as the API would read it.</summary>
    public Dictionary<string, string?> Configuration(GoldenSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var settings = new Dictionary<string, string?>
        {
            ["VectorStore:Provider"] = "InMemory",
            ["Retrieval:Top"] = "6",
        };
        if (Provider == Offline)
        {
            settings["AI:Provider"] = "Offline";
            settings["Retrieval:MinScore"] = set.Offline.MinScore.ToString(CultureInfo.InvariantCulture);
        }
        else if (Provider == AzureOpenAI)
        {
            var identity = AzureIdentityFromEnvironment();
            settings["AI:Provider"] = "AzureOpenAI";
            settings["AI:AzureOpenAI:Endpoint"] = AzureEndpoint ?? throw new InvalidOperationException("Set PRIORAUTHBOT_EVAL_AZURE_OPENAI_ENDPOINT to the Azure OpenAI v1 endpoint.");
            settings["AI:AzureOpenAI:ChatDeployment"] = AzureChatDeployment;
            settings["AI:AzureOpenAI:EmbeddingDeployment"] = AzureEmbeddingDeployment;
            settings["Azure:ManagedIdentityClientId"] = identity.ManagedIdentityClientId;
            settings["Azure:TenantId"] = identity.TenantId;
            settings["Retrieval:MinScore"] = AzureMinScore ?? set.Ollama.MinScore.ToString(CultureInfo.InvariantCulture);
            settings["AI:CacheResponses"] = "true";
            settings["AI:CacheEmbeddings"] = "true";
        }
        else if (Provider == OnnxGenAI)
        {
            settings["AI:Provider"] = "OnnxGenAI";
            settings["AI:OnnxGenAI:ModelPath"] = OnnxModelPath ?? throw new InvalidOperationException("Set PRIORAUTHBOT_TEST_ONNX_MODEL to the model folder (scripts/get-model.py downloads it).");
            settings["AI:OnnxGenAI:ModelId"] = OnnxModelId;
            settings["AI:Ollama:Endpoint"] = OllamaEndpoint ?? throw new InvalidOperationException("Set PRIORAUTHBOT_TEST_OLLAMA: the embeddings and the judge run on Ollama.");
            settings["AI:Ollama:EmbeddingModel"] = EmbeddingModel;
            settings["Retrieval:MinScore"] = set.Ollama.MinScore.ToString(CultureInfo.InvariantCulture);
            settings["AI:CacheResponses"] = "true";
            settings["AI:CacheEmbeddings"] = "true";
        }
        else
        {
            settings["AI:Provider"] = "Ollama";
            settings["AI:Ollama:Endpoint"] = OllamaEndpoint ?? throw new InvalidOperationException("Set PRIORAUTHBOT_TEST_OLLAMA to the Ollama endpoint.");
            settings["AI:Ollama:ChatModel"] = ChatModel;
            settings["AI:Ollama:EmbeddingModel"] = EmbeddingModel;
            settings["Retrieval:MinScore"] = set.Ollama.MinScore.ToString(CultureInfo.InvariantCulture);

            // Repeatable runs: identical requests are answered from the on-disk cache (FileDistributedCache).
            settings["AI:CacheResponses"] = "true";
            settings["AI:CacheEmbeddings"] = "true";
        }

        return settings;
    }
}
