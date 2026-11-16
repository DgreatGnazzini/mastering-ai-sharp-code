namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// Opt-in settings for tests that need real infrastructure. Unset, those tests are skipped, so
/// <c>dotnet test</c> works on any machine with no Docker, model, or network.
/// </summary>
internal static class LiveSettings
{
    /// <summary>An Ollama endpoint, such as <c>http://localhost:11434</c>, with the chat model pulled.</summary>
    public static string? Ollama => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_OLLAMA");

    public static string ChatModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_CHAT_MODEL") ?? "llama3.2:3b";

    /// <summary>A model that reads images, for chapter 4.</summary>
    public static string VisionModel => Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_VISION_MODEL") ?? "qwen2.5vl:3b";

    /// <summary>Folder for reports that CI uploads as artifacts.</summary>
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
        ["AI:Ollama:VisionModel"] = VisionModel,
    };
}
