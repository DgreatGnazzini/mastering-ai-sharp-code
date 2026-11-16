using Microsoft.Extensions.AI;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// A model that plays a script of tool calls and replies, one step per request (chapter 10). It
/// records every request and its options, so a test can see what each round trip of the tool loop
/// sent: the tools offered, and the tool results the model was shown. The last step repeats.
/// </summary>
internal sealed class ScriptedToolModel(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] steps) : IChatClient
{
    private int _next;

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public static Func<IReadOnlyList<ChatMessage>, ChatMessage> Call(string name, Dictionary<string, object?>? arguments = null) =>
        _ => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{Guid.NewGuid():N}", name, arguments ?? [])]);

    public static Func<IReadOnlyList<ChatMessage>, ChatMessage> Reply(string text) =>
        _ => new ChatMessage(ChatRole.Assistant, text);

    /// <summary>The text of every tool result the model has been shown so far in this request.</summary>
    public static string ToolResults(IReadOnlyList<ChatMessage> messages) =>
        string.Join("\n", messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        Options.Add(options);
        var step = steps[Math.Min(_next++, steps.Length - 1)];
        return Task.FromResult(new ChatResponse(step(list)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A clock a test can move.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
