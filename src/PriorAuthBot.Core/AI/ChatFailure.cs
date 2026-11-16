using System.ClientModel;
using System.Net;

namespace PriorAuthBot.Core.AI;

public enum ChatFailureKind
{
    /// <summary>Not a failure we recognize; treat it as a bug.</summary>
    Unknown,

    /// <summary>429 from the provider: too many requests or tokens for the quota.</summary>
    RateLimited,

    /// <summary>The provider took too long.</summary>
    Timeout,

    /// <summary>5xx, or the connection failed: the provider is down or overloaded.</summary>
    Unavailable,

    /// <summary>The provider's content filter blocked the prompt (Azure OpenAI returns 400 with code content_filter).</summary>
    ContentFiltered,

    /// <summary>PriorAuthBot's own guardrails refused the request before it left (chapter 12).</summary>
    Blocked,
}

/// <summary>
/// Classifies the exceptions that providers throw (chapter 2). The OpenAI SDK throws
/// <see cref="ClientResultException"/>; OllamaSharp and raw HTTP throw <see cref="HttpRequestException"/>.
/// Everything above this class deals in <see cref="ChatFailureKind"/>, not in provider types.
/// </summary>
public static class ChatFailure
{
    public static ChatFailureKind Classify(Exception exception) => exception switch
    {
        Security.PromptRejectedException => ChatFailureKind.Blocked,
        Security.PromptShieldUnavailableException => ChatFailureKind.Unavailable,
        TimeoutException => ChatFailureKind.Timeout,
        ClientResultException { Status: 429 } => ChatFailureKind.RateLimited,
        ClientResultException { Status: 400 } e when e.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase) => ChatFailureKind.ContentFiltered,
        ClientResultException { Status: 408 } => ChatFailureKind.Timeout,
        ClientResultException { Status: >= 500 } => ChatFailureKind.Unavailable,
        ClientResultException { Status: 0 } => ChatFailureKind.Unavailable,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => ChatFailureKind.RateLimited,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout } => ChatFailureKind.Timeout,
        HttpRequestException { StatusCode: null } => ChatFailureKind.Unavailable,
        HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => ChatFailureKind.Unavailable,
        _ => ChatFailureKind.Unknown,
    };

    /// <summary>Worth trying again: the same request may succeed in a moment.</summary>
    public static bool IsTransient(ChatFailureKind kind) =>
        kind is ChatFailureKind.RateLimited or ChatFailureKind.Timeout or ChatFailureKind.Unavailable;

    /// <summary>The provider's Retry-After header, when it sent one.</summary>
    public static TimeSpan? RetryAfter(Exception exception)
    {
        if (exception is ClientResultException { } e && e.GetRawResponse() is { } response)
        {
            if (response.Headers.TryGetValue("retry-after-ms", out var ms) && double.TryParse(ms, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }

            if (response.Headers.TryGetValue("Retry-After", out var seconds) && double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var s))
            {
                return TimeSpan.FromSeconds(s);
            }
        }

        return null;
    }
}
