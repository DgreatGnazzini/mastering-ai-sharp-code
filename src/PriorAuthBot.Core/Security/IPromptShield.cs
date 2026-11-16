namespace PriorAuthBot.Core.Security;

/// <summary>
/// What a prompt shield found. <paramref name="UserPromptAttack"/> is a direct attack: the user is
/// trying to override the rules. <paramref name="DocumentAttacks"/> has one entry per document, true
/// where the document carries instructions aimed at the model: an indirect attack.
/// </summary>
public sealed record ShieldVerdict(bool UserPromptAttack, IReadOnlyList<bool> DocumentAttacks, string Detector)
{
    public bool AnyAttack => UserPromptAttack || DocumentAttacks.Any(a => a);

    public static ShieldVerdict Clean(int documents, string detector) => new(false, new bool[documents], detector);
}

/// <summary>
/// Checks text for prompt injection before a model reads it (chapter 12). The two kinds of input
/// are kept apart because they are handled differently: a direct attack blocks the request, and an
/// indirect one removes the document and lets the request go on without it.
/// </summary>
public interface IPromptShield
{
    /// <param name="userPrompt">The user's own words, or null when there are none to check.</param>
    /// <param name="documents">Text from anywhere else: retrieved chunks, tool results, OCR output.</param>
    Task<ShieldVerdict> AnalyzeAsync(string? userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken = default);
}

/// <summary>The shield could not give a verdict, and PriorAuthBot is configured to fail closed.</summary>
public sealed class PromptShieldUnavailableException : Exception
{
    public PromptShieldUnavailableException()
        : base("The prompt shield is unavailable.")
    {
    }

    public PromptShieldUnavailableException(string message)
        : base(message)
    {
    }

    public PromptShieldUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Why a request was refused before it reached the model.</summary>
public enum PromptRejection
{
    /// <summary>The user's message tries to override PriorAuthBot's rules.</summary>
    Injection,

    /// <summary>The user's message is longer than <see cref="SecurityOptions.MaxUserMessageCharacters"/>.</summary>
    TooLong,
}

/// <summary>
/// The guardrails refused a request. The message is PriorAuthBot's own and safe to show: it never
/// repeats the user's text or says which pattern matched, which would teach an attacker what to avoid.
/// </summary>
public sealed class PromptRejectedException : Exception
{
    public PromptRejectedException()
        : this(PromptRejection.Injection)
    {
    }

    public PromptRejectedException(PromptRejection reason)
        : base(reason == PromptRejection.TooLong
            ? "The message is too long. Ask one question at a time."
            : "PriorAuthBot's safety checks blocked this request.") => Reason = reason;

    public PromptRejectedException(string message)
        : base(message)
    {
    }

    public PromptRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PromptRejection Reason { get; }
}
