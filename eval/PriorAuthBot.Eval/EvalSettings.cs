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
    bool RedTeam = false)
{
    public const string Offline = "Offline";
    public const string Ollama = "Ollama";

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
        if (provider is not (Offline or Ollama))
        {
            throw new ArgumentException($"--provider must be {Offline} or {Ollama}, not '{provider}'.");
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
            args.Contains("--redteam"));
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
