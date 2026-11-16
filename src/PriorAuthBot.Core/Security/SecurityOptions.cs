namespace PriorAuthBot.Core.Security;

/// <summary>
/// Chapter 12's guardrails: which prompt shield runs, what the output checks allow, and the switch
/// that turns them all off (for the red-team ablation, never in production).
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// The guardrail middleware on every chat client. On by default. The red-team eval turns it off
    /// once to measure what the guardrails stop; the API refuses to start with it off outside Development.
    /// </summary>
    public bool Guardrails { get; set; } = true;

    public PromptShieldOptions PromptShield { get; set; } = new();

    /// <summary>
    /// The longest user message PriorAuthBot accepts, in characters. A policy question is a sentence or
    /// two; a 50,000-character "question" is an attack on the context window or the bill (OWASP LLM10).
    /// </summary>
    public int MaxUserMessageCharacters { get; set; } = 4_000;

    /// <summary>
    /// Hosts an answer may link to, besides PriorAuthBot's own relative links such as
    /// <c>/documents/{id}</c>. Empty by default: no answer needs to send anyone off site.
    /// </summary>
    public IList<string> AllowedLinkHosts { get; set; } = [];

    /// <summary>
    /// An answer that repeats this many consecutive words of a system prompt is treated as a system
    /// prompt leak and replaced. Twelve is longer than the one sentence a prompt asks the model to
    /// repeat word for word (the decline, "I don't know based on the documents you can access", is 11
    /// words as the check counts them) and long enough that a shared phrase is no accident.
    /// </summary>
    public int SystemPromptOverlapWords { get; set; } = 12;
}

public sealed class PromptShieldOptions
{
    /// <summary><c>Heuristic</c> (the default, runs anywhere), <c>ContentSafety</c> (Azure AI Content Safety Prompt Shields), or <c>None</c>.</summary>
    public string Provider { get; set; } = PromptShieldProviders.Heuristic;

    /// <summary>The Content Safety resource, such as https://dd-contentsafety.cognitiveservices.azure.com/.</summary>
    public Uri? Endpoint { get; set; }

    public string ApiVersion { get; set; } = "2024-09-01";

    /// <summary>
    /// Requests the service accepts at once. Prompt Shields limits the length of the prompt, the number
    /// of documents, and their total length per call; check the service's current quotas and set these to match.
    /// </summary>
    public int MaxDocumentsPerCall { get; set; } = 5;

    public int MaxCharactersPerCall { get; set; } = 10_000;

    /// <summary>
    /// What to do when the shield service fails: <c>Block</c> (the default) refuses the request with a
    /// 503, <c>Heuristic</c> falls back to the local checks and logs a warning. Never "let it through".
    /// </summary>
    public string OnError { get; set; } = "Block";
}

public static class PromptShieldProviders
{
    public const string Heuristic = "Heuristic";
    public const string ContentSafety = "ContentSafety";
    public const string None = "None";
}
