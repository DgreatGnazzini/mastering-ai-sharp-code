using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// The chat client used when <c>AI:Provider</c> is <c>Offline</c>. It never calls a model: it
/// echoes the first source it was given, so the API runs end to end in tests and CI.
/// </summary>
public sealed class OfflineChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var context = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var start = context.IndexOf("[1]", StringComparison.Ordinal);
        var text = start < 0
            ? "Offline mode has no model to answer with."
            : "Offline mode (no model). The most relevant source says: " + context[start..].Split('\n', 3).ElementAtOrDefault(1)?.Trim() + " [1]";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
