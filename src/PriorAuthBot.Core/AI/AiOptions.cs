namespace PriorAuthBot.Core.AI;

/// <summary>
/// Which model provider PriorAuthBot talks to. Chapter 2 introduces the chat side and chapter 5
/// the embedding side. Readers switch providers in configuration, never in code.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>
    /// <c>Ollama</c> (local, the default), <c>OpenAI</c>, or <c>Offline</c>. Offline uses a
    /// deterministic hashing embedder and no chat model, so tests and CI run with no network.
    /// </summary>
    public string Provider { get; set; } = "Ollama";

    public OllamaOptions Ollama { get; set; } = new();

    public OpenAIOptions OpenAI { get; set; } = new();
}

public sealed class OllamaOptions
{
    public Uri Endpoint { get; set; } = new("http://localhost:11434");

    public string ChatModel { get; set; } = "llama3.2:3b";

    /// <summary>nomic-embed-text returns 768 dimensions, which matches <see cref="Retrieval.ChunkRecord"/>.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text:v1.5";
}

public sealed class OpenAIOptions
{
    /// <summary>Set with user secrets or an environment variable, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public string ChatModel { get; set; } = "gpt-4.1-mini";

    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
}
