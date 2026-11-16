using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Privacy;

namespace PriorAuthBot.Core.AI;

/// <summary>The request would have sent PHI to a provider that is not approved for it.</summary>
public sealed class PhiNotAllowedException(string message) : InvalidOperationException(message)
{
    public PhiNotAllowedException()
        : this("This provider is not approved for PHI.")
    {
    }

    public PhiNotAllowedException(string message, Exception innerException)
        : this(message + " " + innerException?.Message)
    {
    }
}

/// <summary>
/// Middleware for a provider with no business associate agreement (chapter 4). Text is redacted
/// before it leaves; images and files are refused outright, because a scan cannot be redacted with
/// a regular expression. Whether a provider is approved is a contract question, answered in
/// configuration by whoever signed the BAA, not by this class.
/// </summary>
public sealed class PhiGuardChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Guard(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(Guard(messages), options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public static List<ChatMessage> Guard(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<ChatMessage> guarded = [];
        foreach (var message in messages)
        {
            List<AIContent> contents = [];
            foreach (var content in message.Contents)
            {
                contents.Add(content switch
                {
                    TextContent text => new TextContent(PhiRedactor.Redact(text.Text).Text),
                    DataContent or UriContent => throw new PhiNotAllowedException(
                        "Images and files are not sent to this provider: it is not approved for PHI. Set AI:ApprovedForPhi only if a BAA covers it."),
                    _ => content,
                });
            }

            guarded.Add(new ChatMessage(message.Role, contents) { AuthorName = message.AuthorName });
        }

        return guarded;
    }
}
