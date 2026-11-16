using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// A chat client that plays a script, one step per call: throw an exception, or reply. Streaming
/// replies come back one word at a time, and a step can throw after its first word.
/// </summary>
internal sealed class ScriptedChatClient(params ScriptedChatClient.Step[] steps) : IChatClient
{
    private readonly Queue<Step> _steps = new(steps);

    public int Calls { get; private set; }

    public static Step Reply(string text, ChatFinishReason? finishReason = null) => new(text, null, false, finishReason);

    public static Step Fail(Exception exception) => new(null, exception, false, null);

    public static Step FailAfterFirstWord(string text, Exception exception) => new(text, exception, true, null);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var step = Next();
        return step.Exception is not null
            ? Task.FromException<ChatResponse>(step.Exception)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, step.Text)) { FinishReason = step.FinishReason ?? ChatFinishReason.Stop });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var step = Next();
        await Task.Yield();
        if (step.Exception is not null && !step.FailMidStream)
        {
            throw step.Exception;
        }

        var words = (step.Text ?? string.Empty).Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (i == 1 && step.FailMidStream)
            {
                throw step.Exception!;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, i == 0 ? words[i] : " " + words[i])
            {
                FinishReason = i == words.Length - 1 ? step.FinishReason ?? ChatFinishReason.Stop : null,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private Step Next()
    {
        Calls++;
        return _steps.Count > 1 ? _steps.Dequeue() : _steps.Peek();
    }

    internal sealed record Step(string? Text, Exception? Exception, bool FailMidStream, ChatFinishReason? FinishReason);
}
