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
    /// Offline uses a deterministic hashing embedder and no chat model, so tests and CI run with no network.
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

    /// <summary>
    /// Whether this provider may receive PHI (chapter 4). Leave it unset to get the default: true for
    /// Ollama and Offline, which run on your own machines, and false for hosted providers. Set it to
    /// true for Azure OpenAI only once a business associate agreement covers the subscription.
    /// When false, text is redacted and images are refused before any request leaves.
    /// </summary>
    public bool? ApprovedForPhi { get; set; }

    public bool IsApprovedForPhi => ApprovedForPhi ?? Provider is "Ollama" or "Offline";
}

public sealed class OllamaOptions
{
    public Uri Endpoint { get; set; } = new("http://localhost:11434");

    public string ChatModel { get; set; } = "llama3.2:3b";

    /// <summary>A smaller, faster model for classification. Empty means "use <see cref="ChatModel"/>".</summary>
    public string? ClassifierModel { get; set; }

    /// <summary>nomic-embed-text returns 768 dimensions, which matches <see cref="Retrieval.ChunkRecord"/>.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text:v1.5";

    /// <summary>A model that accepts images, for OCR and form extraction (chapter 4).</summary>
    public string VisionModel { get; set; } = "qwen2.5vl:3b";

    /// <summary>
    /// Context window for the vision model, in tokens. Ollama defaults to 4,096. Image tokens grow with
    /// pixels: a letter page at 100 dpi is about 1,200 for qwen2.5vl, and at 200 dpi about 4,100, which
    /// Ollama refuses before the model reads anything. 8,192 leaves room for a sharper scan.
    /// </summary>
    public int VisionContextLength { get; set; } = 8192;

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

    /// <summary>A deployment that accepts images. Empty means "use <see cref="ChatDeployment"/>", which does for GPT-4.1 models.</summary>
    public string? VisionDeployment { get; set; }

    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
}

public sealed class OpenAIOptions
{
    /// <summary>Set with user secrets or an environment variable, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public string ChatModel { get; set; } = "gpt-4.1-mini";

    public string? ClassifierModel { get; set; }

    public string? VisionModel { get; set; }

    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
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
