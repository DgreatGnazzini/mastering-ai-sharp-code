namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// Opt-in settings for tests that need real infrastructure. Unset, those tests are skipped, so
/// <c>dotnet test</c> works on any machine with no Docker, model, or network.
/// </summary>
internal static class LiveSettings
{
    /// <summary>PostgreSQL with pgvector, such as <c>Host=localhost;Database=priorauthbot_test;Username=postgres;Password=postgres</c>.</summary>
    public static string? Postgres => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_POSTGRES");

    /// <summary>An Ollama endpoint, such as <c>http://localhost:11434</c>, with the chat and embedding models pulled.</summary>
    public static string? Ollama => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_OLLAMA");

    public static string ChatModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_CHAT_MODEL") ?? "llama3.2:3b";

    /// <summary>
    /// The model that grades groundedness. A judge must follow the evaluator's output format
    /// exactly, which small models often fail to do, so it can be larger than the chat model.
    /// </summary>
    public static string JudgeModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_JUDGE_MODEL") ?? ChatModel;

    public static string EmbeddingModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_EMBEDDING_MODEL") ?? "nomic-embed-text:v1.5";

    /// <summary>Folder for reports (chunking table, groundedness scores) that CI uploads as artifacts.</summary>
    public static string ReportDirectory
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_REPORTS") ?? Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static Dictionary<string, string?> OllamaConfiguration() => new()
    {
        ["AI:Provider"] = "Ollama",
        ["AI:Ollama:Endpoint"] = Ollama,
        ["AI:Ollama:ChatModel"] = ChatModel,
        ["AI:Ollama:EmbeddingModel"] = EmbeddingModel,
        ["Retrieval:MinScore"] = GoldenSet.Load().Ollama.MinScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
