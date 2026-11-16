using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Security;

namespace PriorAuthBot.Core.Rag;

/// <summary>
/// Retrieve, augment, generate (chapter 7). The caller's identity flows into retrieval, so the
/// context, and therefore the answer, contains only what that caller can see. Chapter 12 adds a
/// screen between retrieval and the prompt: chunks that carry instructions for the model are dropped
/// before they are numbered, so the answer never cites them.
/// </summary>
public sealed partial class RagAnswerService(
    SecureRetriever retriever,
    ContextAssembler assembler,
    IChatClient chatClient,
    ILogger<RagAnswerService> logger,
    IPromptShield? shield = null,
    Microsoft.Extensions.Options.IOptions<SecurityOptions>? security = null)
{
    private readonly IPromptShield? _shield = security?.Value.Guardrails == false ? null : shield;

    /// <summary>
    /// The one way PriorAuthBot declines. It is the same whether nothing exists or nothing is
    /// visible to the caller, so a decline never reveals that a restricted document exists.
    /// </summary>
    public const string DeclineMessage = "I don't know based on the documents you can access.";

    public async Task<RagAnswer> AskAsync(string question, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(caller);

        // Chapter 12: check the question before it costs an embedding and a search, then the chunks after.
        await ScreenQuestionAsync(question, caller, cancellationToken).ConfigureAwait(false);
        var retrieved = await retriever.SearchAsync(question, caller, cancellationToken).ConfigureAwait(false);
        retrieved = await ScreenAsync(question, retrieved, caller, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The prompt, built in one place so tests and the groundedness check see exactly what the model
    /// sees. Chapter 12: every source is its own untrusted block, and the question comes after them,
    /// outside every block, as the only text in the message that is the user's.
    /// </summary>
    public static List<ChatMessage> BuildMessages(string question, AssembledContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<AIContent> contents = [];
        for (var i = 0; i < context.Blocks.Count; i++)
        {
            contents.Add(UntrustedContent.Mark(context.Blocks[i], (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        contents.Add(new TextContent($"Question: {question}"));
        return
        [
            new(ChatRole.System, PromptLibrary.Get(PromptLibrary.GroundedAnswer)),
            new(ChatRole.User, contents),
        ];
    }

    /// <summary>
    /// Checks the question and the retrieved chunks with the prompt shield before the prompt is built.
    /// A direct attack in the question refuses the request. A chunk that carries instructions is left
    /// out, and the rest of the answer goes ahead without it. The guardrail middleware would catch the
    /// same chunk later; dropping it here keeps it out of the numbering and the citations.
    /// </summary>
    private async Task ScreenQuestionAsync(string question, CallerIdentity caller, CancellationToken cancellationToken)
    {
        if (_shield is null)
        {
            return;
        }

        if (question.Length > (security?.Value.MaxUserMessageCharacters ?? int.MaxValue))
        {
            throw new PromptRejectedException(PromptRejection.TooLong);
        }

        if ((await _shield.AnalyzeAsync(question, [], cancellationToken).ConfigureAwait(false)).UserPromptAttack)
        {
            LogBlocked(logger, caller.UserId);
            throw new PromptRejectedException(PromptRejection.Injection);
        }
    }

    private async Task<IReadOnlyList<RetrievedChunk>> ScreenAsync(string question, IReadOnlyList<RetrievedChunk> retrieved, CallerIdentity caller, CancellationToken cancellationToken)
    {
        if (_shield is null || retrieved.Count == 0)
        {
            return retrieved;
        }

        var verdict = await _shield.AnalyzeAsync(question, [.. retrieved.Select(r => r.Record.Text)], cancellationToken).ConfigureAwait(false);
        if (verdict.UserPromptAttack)
        {
            LogBlocked(logger, caller.UserId);
            throw new PromptRejectedException(PromptRejection.Injection);
        }

        var kept = retrieved.Where((_, i) => !verdict.DocumentAttacks[i]).ToList();
        if (kept.Count < retrieved.Count)
        {
            LogQuarantined(logger, retrieved.Count - kept.Count, caller.UserId,
                string.Join(", ", retrieved.Where((_, i) => verdict.DocumentAttacks[i]).Select(r => r.Record.ChunkId)));
        }

        return kept;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Blocked a question from {UserId}: the prompt shield flagged it as an injection attempt.")]
    private static partial void LogBlocked(ILogger logger, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Left {Count} retrieved chunk(s) out of an answer for {UserId}: the prompt shield flagged {ChunkIds}.")]
    private static partial void LogQuarantined(ILogger logger, int count, string userId, string chunkIds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed {Count} citation markers that pointed at no source, in an answer for {UserId}.")]
    private static partial void LogInvalidCitations(ILogger logger, string userId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Declined for {UserId}: {Reason}.")]
    private static partial void LogDeclined(ILogger logger, string userId, string reason);
}
