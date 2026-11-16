using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;

namespace PriorAuthBot.Api;

/// <summary>
/// <c>POST /chat</c> (chapter 2): streams the model's reply as server-sent events. The first
/// update is awaited before the response starts, so a provider failure becomes a proper HTTP
/// status instead of a stream that breaks after a 200.
/// </summary>
internal static partial class ChatEndpoint
{
    public static async Task<IResult> HandleAsync(ChatRequest request, IChatClient chatClient, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.Problem("Message is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var logger = loggers.CreateLogger(typeof(ChatEndpoint));
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are PriorAuthBot, the internal assistant for Deductible Dynamics staff. Keep answers short. You have no access to company documents yet, so say so when asked about a policy."),
            new(ChatRole.User, request.Message),
        ];

        var enumerator = chatClient.GetStreamingResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool hasFirst;
        try
        {
            hasFirst = await enumerator.MoveNextAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await enumerator.DisposeAsync();
            return ToProblem(ex, logger);
        }

        return TypedResults.ServerSentEvents(StreamAsync(enumerator, hasFirst, logger, cancellationToken));
    }

    /// <summary>Maps a provider failure to the status a client can act on. Never echoes the provider's message.</summary>
    public static IResult ToProblem(Exception exception, ILogger logger)
    {
        var kind = exception is OperationCanceledException ? ChatFailureKind.Timeout : ChatFailure.Classify(exception);
        LogFailed(logger, exception, kind);
        return kind switch
        {
            ChatFailureKind.RateLimited => Results.Problem("The model is busy. Try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable),
            ChatFailureKind.Unavailable => Results.Problem("The model is unavailable. Try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable),
            ChatFailureKind.Timeout => Results.Problem("The model took too long to respond.", statusCode: StatusCodes.Status504GatewayTimeout),
            ChatFailureKind.ContentFiltered => Results.Problem("The request was blocked by the model provider's content filter.", statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.Problem("Something went wrong talking to the model.", statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator, bool hasFirst, ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var _ = enumerator;
        var more = hasFirst;
        while (more)
        {
            var update = enumerator.Current;
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new SseItem<string>(update.Text, "delta");
            }

            if (update.FinishReason == ChatFinishReason.ContentFilter)
            {
                // The answer was cut off by the provider's output filter. Say so instead of ending mid-sentence.
                yield return new SseItem<string>("The rest of this answer was blocked by the content filter.", "filtered");
            }

            var failed = false;
            try
            {
                more = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Headers are already sent, so a status code is no longer possible. Tell the client in-band.
                LogFailed(logger, ex, ChatFailure.Classify(ex));
                failed = true;
            }

            if (failed)
            {
                yield return new SseItem<string>("The answer was interrupted. Try again.", "error");
                yield break;
            }
        }

        yield return new SseItem<string>(string.Empty, "done");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat request failed: {Kind}.")]
    private static partial void LogFailed(ILogger logger, Exception exception, ChatFailureKind kind);
}
