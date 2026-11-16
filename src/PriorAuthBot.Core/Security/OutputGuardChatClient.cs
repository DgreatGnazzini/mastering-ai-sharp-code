using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// The output half of PriorAuthBot's guardrails (chapter 12): every reply passes through
/// <see cref="OutputGuard"/> before the code that asked for it sees it. Function calls, usage, and
/// everything else that is not text pass through untouched.
/// <para>
/// Streaming is held back one line at a time. A link or an image is only recognizable once it is
/// complete, and a token stream splits <c>![x](https://...)</c> across many updates, so the client
/// gets each line when it ends, already checked. A reply that turns into a system prompt leak
/// partway through stops there, with the refusal in place of the rest.
/// </para>
/// </summary>
public sealed partial class OutputGuardChatClient(IChatClient innerClient, OutputGuard guard, ILogger<OutputGuardChatClient> logger)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        var (system, ground, complete) = Split(list);

        var whole = guard.Check(response.Text, system, ground, complete);
        if (whole.SystemPromptLeak)
        {
            LogChanged(logger, "system prompt leak", 0, 0, 0);
            return Replace(response, _ => null, OutputGuard.SystemPromptRefusal);
        }

        var redacted = 0;
        var markdown = new SanitizeResult(string.Empty, 0, 0, 0);
        var result = Replace(response, text =>
        {
            var check = guard.Check(text, system, ground, complete);
            redacted += check.IdentifiersRedacted;
            markdown = new SanitizeResult(string.Empty, markdown.ImagesRemoved + check.Markdown.ImagesRemoved, markdown.LinksRemoved + check.Markdown.LinksRemoved, markdown.HtmlRemoved + check.Markdown.HtmlRemoved);
            return check.Text;
        }, null);
        if (redacted > 0 || markdown.Total > 0)
        {
            LogChanged(logger, "sanitized", redacted, markdown.ImagesRemoved, markdown.LinksRemoved + markdown.HtmlRemoved);
        }

        return result;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var (system, ground, complete) = Split(list);
        var pending = new StringBuilder();
        var sent = new StringBuilder();
        ChatResponseUpdate? template = null;

        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
        {
            template ??= update;
            var others = update.Contents.Where(c => c is not TextContent).ToList();
            pending.Append(update.Text);
            var cut = pending.ToString().LastIndexOf('\n');
            string? ready = null;
            if (cut >= 0)
            {
                ready = pending.ToString(0, cut + 1);
                pending.Remove(0, cut + 1);
            }

            if (ready is not null)
            {
                sent.Append(ready);
                if (guard.Check(sent.ToString(), system, ground, complete).SystemPromptLeak)
                {
                    LogChanged(logger, "system prompt leak", 0, 0, 0);
                    yield return Text(update, "\n" + OutputGuard.SystemPromptRefusal);
                    yield break;
                }

                yield return Text(update, guard.Check(ready, system, ground, complete).Text, others);
            }
            else if (others.Count > 0 || update.FinishReason is not null)
            {
                yield return Text(update, string.Empty, others);
            }
        }

        if (pending.Length > 0 && template is not null)
        {
            sent.Append(pending);
            yield return guard.Check(sent.ToString(), system, ground, complete).SystemPromptLeak
                ? Text(template, "\n" + OutputGuard.SystemPromptRefusal)
                : Text(template, guard.Check(pending.ToString(), system, ground, complete).Text);
        }
    }

    // Ground is everything the model read apart from the system prompt. An image or file in the request
    // (a scanned intake form, chapter 4) is something the model read that the ground text cannot show.
    private static (List<string> System, string Ground, bool Complete) Split(IList<ChatMessage> messages)
    {
        var system = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text).ToList();
        var complete = !messages.SelectMany(m => m.Contents).Any(c => c is DataContent or UriContent);
        var ground = new StringBuilder();
        foreach (var message in messages.Where(m => m.Role != ChatRole.System))
        {
            foreach (var content in message.Contents)
            {
                ground.Append(content switch
                {
                    TextContent t => t.Text,
                    FunctionResultContent r => PromptShieldChatClient.ResultText(r),
                    FunctionCallContent c => c.Arguments is null ? string.Empty : string.Join(' ', c.Arguments.Values),
                    _ => string.Empty,
                }).Append('\n');
            }
        }

        return (system, ground.ToString(), complete);
    }

    /// <summary>The response with each text rewritten, or all text replaced by <paramref name="replacement"/>.</summary>
    private static ChatResponse Replace(ChatResponse response, Func<string, string?> rewrite, string? replacement)
    {
        var placed = false;
        var messages = new List<ChatMessage>();
        foreach (var message in response.Messages)
        {
            var contents = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                if (content is not TextContent text)
                {
                    contents.Add(content);
                }
                else if (replacement is null)
                {
                    contents.Add(new TextContent(rewrite(text.Text)) { AdditionalProperties = text.AdditionalProperties });
                }
                else if (!placed)
                {
                    contents.Add(new TextContent(replacement));
                    placed = true;
                }
            }

            messages.Add(new ChatMessage(message.Role, contents) { AuthorName = message.AuthorName, MessageId = message.MessageId, AdditionalProperties = message.AdditionalProperties });
        }

        return new ChatResponse(messages)
        {
            ResponseId = response.ResponseId,
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            AdditionalProperties = response.AdditionalProperties,
            RawRepresentation = response.RawRepresentation,
        };
    }

    private static ChatResponseUpdate Text(ChatResponseUpdate source, string text, IList<AIContent>? others = null)
    {
        List<AIContent> contents = text.Length > 0 ? [new TextContent(text)] : [];
        contents.AddRange(others ?? []);
        return new ChatResponseUpdate(source.Role ?? ChatRole.Assistant, contents)
        {
            ResponseId = source.ResponseId,
            MessageId = source.MessageId,
            ConversationId = source.ConversationId,
            ModelId = source.ModelId,
            CreatedAt = source.CreatedAt,
            FinishReason = source.FinishReason,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Output guard changed a reply ({What}): {Identifiers} identifiers redacted, {Images} images and {Links} links or tags removed.")]
    private static partial void LogChanged(ILogger logger, string what, int identifiers, int images, int links);
}
