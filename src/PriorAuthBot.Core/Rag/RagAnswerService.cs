using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Rag;

/// <summary>
/// Retrieve, augment, generate (chapter 7). The caller's identity flows into retrieval, so the
/// context, and therefore the answer, contains only what that caller can see.
/// </summary>
public sealed partial class RagAnswerService(
    SecureRetriever retriever,
    ContextAssembler assembler,
    IChatClient chatClient,
    ILogger<RagAnswerService> logger)
{
    /// <summary>
    /// The one way PriorAuthBot declines. It is the same whether nothing exists or nothing is
    /// visible to the caller, so a decline never reveals that a restricted document exists.
    /// </summary>
    public const string DeclineMessage = "I don't know based on the documents you can access.";

    public async Task<RagAnswer> AskAsync(string question, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(caller);

        var retrieved = await retriever.SearchAsync(question, caller, cancellationToken).ConfigureAwait(false);
        var context = assembler.Assemble(retrieved);
        if (context.Sources.Count == 0)
        {
            // Nothing relevant is visible: decline without calling the model at all.
            LogDeclined(logger, caller.UserId, "no relevant sources");
            return new RagAnswer(DeclineMessage, Declined: true, Sources: []);
        }

        var messages = BuildMessages(question, context);
        var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Temperature = 0 }, cancellationToken).ConfigureAwait(false);
        var text = response.Text.Trim();

        if (text.Length == 0 || text.Contains("I don't know", StringComparison.OrdinalIgnoreCase))
        {
            LogDeclined(logger, caller.UserId, "model found no answer in the sources");
            return new RagAnswer(DeclineMessage, Declined: true, Sources: []);
        }

        // Structured citations (chapter 8): resolve the [n] markers against the sources the model was given.
        var (cleaned, cited, removed) = Citations.Resolve(text, context.Sources.Count);
        if (removed > 0)
        {
            LogInvalidCitations(logger, caller.UserId, removed);
        }

        var sources = context.Sources
            .Select((s, i) => Citations.Reference(i + 1, s.Record, cited.Contains(i + 1)))
            .ToList();
        return new RagAnswer(cleaned, Declined: false, sources);
    }

    /// <summary>The prompt, built in one place so tests and the groundedness check see exactly what the model sees.</summary>
    public static List<ChatMessage> BuildMessages(string question, AssembledContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return
        [
            new(ChatRole.System, PromptLibrary.Get(PromptLibrary.GroundedAnswer)),
            new(ChatRole.User, $"<sources>\n{context.Text}\n</sources>\n\nQuestion: {question}"),
        ];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed {Count} citation markers that pointed at no source, in an answer for {UserId}.")]
    private static partial void LogInvalidCitations(ILogger logger, string userId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Declined for {UserId}: {Reason}.")]
    private static partial void LogDeclined(ILogger logger, string userId, string reason);
}
