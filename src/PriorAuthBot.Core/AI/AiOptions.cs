namespace PriorAuthBot.Core.AI;

/// <summary>
/// Which model provider PriorAuthBot talks to, and how. Chapter 2 introduces the chat side and
/// chapter 5 the embedding side. Readers switch providers in configuration, never in code.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>
    /// <c>Ollama</c> (local, the default), <c>AzureOpenAI</c>, <c>OpenAI</c>, or <c>Offline</c>.
    /// Offline uses no model at all, so tests and CI run with no network.
    /// </summary>
    public string Provider { get; set; } = "Ollama";

    public OllamaOptions Ollama { get; set; } = new();

    public AzureOpenAIOptions AzureOpenAI { get; set; } = new();

    public OpenAIOptions OpenAI { get; set; } = new();

    public ResilienceOptions Resilience { get; set; } = new();

    /// <summary>
    /// Cache identical requests in memory. Off by default: a cache holds prompts and answers,
    /// so turn it on only where that data is allowed to sit (see chapter 2's security review).
    /// </summary>
    public bool CacheResponses { get; set; }
}

public sealed class OllamaOptions
{
    public Uri Endpoint { get; set; } = new("http://localhost:11434");

    public string ChatModel { get; set; } = "llama3.2:3b";

    /// <summary>A smaller, faster model for classification. Empty means "use <see cref="ChatModel"/>".</summary>
    public string? ClassifierModel { get; set; }

    /// <summary>
    /// Local models on a CPU can take minutes to read a long prompt. HttpClient's default of
    /// 100 seconds is too short for that, so the timeout is configurable.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class AzureOpenAIOptions
{
    /// <summary>The resource's v1 endpoint, such as <c>https://dd-priorauthbot.openai.azure.com/openai/v1/</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Deployment names, not model names: Azure routes by deployment.</summary>
    public string ChatDeployment { get; set; } = "gpt-4.1-mini";

    public string? ClassifierDeployment { get; set; }
}

public sealed class OpenAIOptions
{
    /// <summary>Set with user secrets or an environment variable, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public string ChatModel { get; set; } = "gpt-4.1-mini";

    public string? ClassifierModel { get; set; }
}

public sealed class ResilienceOptions
{
    /// <summary>Attempts per request, including the first. Retries happen only for transient failures.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>First retry delay; each later one doubles, with jitter, unless the provider sends Retry-After.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest PriorAuthBot will wait before one retry, even if Retry-After asks for more.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(20);
}
