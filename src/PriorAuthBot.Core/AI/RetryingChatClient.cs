using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// Middleware that retries transient provider failures (chapter 2): 429, 5xx, timeouts, and
/// dropped connections. It waits for Retry-After when the provider sends it, and otherwise backs
/// off exponentially with jitter. A streaming call is retried only until the first update arrives;
/// after that the caller has already seen part of the answer, and a retry would repeat it.
/// </summary>
public sealed partial class RetryingChatClient(IChatClient innerClient, ResilienceOptions options, ILogger? logger = null, TimeProvider? timeProvider = null)
    : DelegatingChatClient(innerClient)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt, cancellationToken))
            {
                await WaitAsync(ex, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            // Retry until the first update arrives.
            for (var attempt = 1; ; attempt++)
            {
                enumerator = base.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    break;
                }
                catch (Exception ex) when (ShouldRetry(ex, attempt, cancellationToken))
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                    enumerator = null;
                    await WaitAsync(ex, attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            // From here on, failures go to the caller.
            do
            {
                yield return enumerator.Current;
            }
            while (await enumerator.MoveNextAsync().ConfigureAwait(false));
        }
        finally
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private bool ShouldRetry(Exception exception, int attempt, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && attempt < options.MaxAttempts
        && ChatFailure.IsTransient(ChatFailure.Classify(exception));

    private async Task WaitAsync(Exception exception, int attempt, CancellationToken cancellationToken)
    {
        var delay = ChatFailure.RetryAfter(exception)
            ?? TimeSpan.FromMilliseconds(options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (0.8 + (Random.Shared.NextDouble() * 0.4)));
        if (delay > options.MaxDelay)
        {
            delay = options.MaxDelay;
        }

        LogRetry(_logger, ChatFailure.Classify(exception), attempt, options.MaxAttempts, delay.TotalMilliseconds);
        await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model call failed ({Kind}) on attempt {Attempt} of {MaxAttempts}; retrying in {DelayMs:0} ms.")]
    private static partial void LogRetry(ILogger logger, ChatFailureKind kind, int attempt, int maxAttempts, double delayMs);
}
