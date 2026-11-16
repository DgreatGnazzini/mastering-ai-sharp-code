using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// The input half of PriorAuthBot's guardrails (chapter 12), as chat client middleware, so every
/// model call gets it: answers, tools, agents, and workflow steps. Before a request leaves it:
/// <list type="number">
/// <item>Refuses a user message over the length limit.</item>
/// <item>Sends the user's newest message and every piece of untrusted content (marked text and tool
/// results, including MCP tools) to the <see cref="IPromptShield"/>.</item>
/// <item>Refuses the request if the user's message is an attack (<see cref="PromptRejectedException"/>).</item>
/// <item>Replaces each flagged document or tool result with a notice and sends the rest. The user
/// asked an honest question; one poisoned source should not cost them the answer.</item>
/// </list>
/// Logs carry counts and source labels, never the text: the text may be the attacker's, or PHI.
/// </summary>
public sealed partial class PromptShieldChatClient(IChatClient innerClient, IPromptShield shield, SecurityOptions options, ILogger<PromptShieldChatClient> logger)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await base.GetResponseAsync(await GuardAsync(messages, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var guarded = await GuardAsync(messages, cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(guarded, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public async Task<IList<ChatMessage>> GuardAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        // The user's own words: the trusted text of the newest user message. Earlier turns were checked when they were new.
        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User);
        var userText = lastUser is null
            ? null
            : string.Concat(lastUser.Contents.OfType<TextContent>().Where(c => !UntrustedContent.IsUntrusted(c)).Select(c => c.Text));
        if (userText is { Length: var length } && length > options.MaxUserMessageCharacters)
        {
            LogRejected(logger, nameof(PromptRejection.TooLong), length);
            throw new PromptRejectedException(PromptRejection.TooLong);
        }

        var documents = new List<(int Message, int Content, string Label, string Text)>();
        for (var m = 0; m < list.Count; m++)
        {
            var contents = list[m].Contents;
            for (var c = 0; c < contents.Count; c++)
            {
                switch (contents[c])
                {
                    case TextContent text when UntrustedContent.IsUntrusted(text):
                        documents.Add((m, c, UntrustedContent.LabelOf(text), text.Text));
                        break;
                    case FunctionResultContent result:
                        documents.Add((m, c, "tool result", ResultText(result)));
                        break;
                }
            }
        }

        var verdict = await shield.AnalyzeAsync(string.IsNullOrWhiteSpace(userText) ? null : userText, [.. documents.Select(d => d.Text)], cancellationToken).ConfigureAwait(false);
        if (verdict.UserPromptAttack)
        {
            LogRejected(logger, nameof(PromptRejection.Injection), userText?.Length ?? 0);
            throw new PromptRejectedException(PromptRejection.Injection);
        }

        var flagged = documents.Where((_, i) => verdict.DocumentAttacks[i]).ToList();
        if (flagged.Count == 0)
        {
            return list;
        }

        LogRemoved(logger, flagged.Count, string.Join(", ", flagged.Select(f => f.Label)), verdict.Detector);
        var copy = list.Select(m => new ChatMessage(m.Role, [.. m.Contents]) { AuthorName = m.AuthorName, AdditionalProperties = m.AdditionalProperties, MessageId = m.MessageId }).ToList();
        foreach (var (m, c, label, _) in flagged)
        {
            copy[m].Contents[c] = copy[m].Contents[c] is FunctionResultContent result
                ? new FunctionResultContent(result.CallId, UntrustedContent.RemovedNotice)
                : UntrustedContent.Removed(label);
        }

        return copy;
    }

    /// <summary>A tool result as text, the way the model will read it.</summary>
    public static string ResultText(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Result switch
        {
            null => string.Empty,
            string s => s,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? string.Empty,
            var other => JsonSerializer.Serialize(other, AIJsonUtilities.DefaultOptions),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guardrails refused a request ({Reason}, {Length} characters).")]
    private static partial void LogRejected(ILogger logger, string reason, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guardrails removed {Count} untrusted item(s) flagged by the {Detector} shield: {Labels}.")]
    private static partial void LogRemoved(ILogger logger, int count, string labels, string detector);
}
