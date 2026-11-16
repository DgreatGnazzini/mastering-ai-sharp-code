using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Caching;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Security;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Rag;

/// <summary>
/// Retrieve, augment, generate (chapter 7). The caller's identity flows into retrieval, so the
/// context, and therefore the answer, contains only what that caller can see. Chapter 12 adds a
/// screen between retrieval and the prompt: chunks that carry instructions for the model are dropped
/// before they are numbered, so the answer never cites them. Chapter 13 adds a span, the audit
/// context's question and answer, and the answer cache, which is checked after the question screen
/// (an attack is never served from the cache) and before retrieval (a hit costs no search).
/// </summary>
public sealed partial class RagAnswerService(
    SecureRetriever retriever,
    ContextAssembler assembler,
    IChatClient chatClient,
    ILogger<RagAnswerService> logger,
    IPromptShield? shield = null,
    Microsoft.Extensions.Options.IOptions<SecurityOptions>? security = null,
    SemanticAnswerCache? cache = null)
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

        // Chapter 13: the span says what happened, in counts and flags. The audit context, which only
        // the audit log reads, gets the question and the answer.
        using var activity = PriorAuthBotTelemetry.Source.StartActivity("priorauthbot.ask");
        var request = AiRequestContext.Current;
        request?.SetCaller(caller);
        request?.SetPrompt(question);
        if (request is not null)
        {
            activity?.SetTag(PriorAuthBotTelemetry.Tags.AuditId, request.AuditId);
        }

        var answer = await AnswerAsync(question, caller, request, cancellationToken).ConfigureAwait(false);
        request?.SetResponse(answer.Answer);
        request?.MarkCited(answer.Citations.Select(c => c.DocumentId));
        activity?.SetTag(PriorAuthBotTelemetry.Tags.Declined, answer.Declined);
        activity?.SetTag("priorauthbot.answer.citations", answer.Citations.Count);
        return answer;
    }

    private async Task<RagAnswer> AnswerAsync(string question, CallerIdentity caller, AiRequestContext? request, CancellationToken cancellationToken)
    {
        // Chapter 12: check the question before it costs an embedding and a search, then the chunks after.
        await ScreenQuestionAsync(question, caller, cancellationToken).ConfigureAwait(false);

        // Chapter 13: an answer already given to this exact permission set, for this question or a close paraphrase.
        if (cache is { Enabled: true } && await cache.GetAsync(question, caller, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            request?.MarkAnswerCacheHit();
            request?.RecordDocuments(cached.Documents);
            LogCacheHit(logger, caller.UserId, cached.Similarity);
            return cached.Answer;
        }

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
        var chatOptions = new ChatOptions { Temperature = 0 };
        if (context.Sources.Any(s => s.Record.ContainsPhi))
        {
            // Chapter 15: a source marked phi: true makes the whole prompt PHI. With hybrid routing on,
            // this flag sends it to the local model; without routing, nothing reads it.
            PhiRouting.Flag(chatOptions);
        }

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
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
        var answer = new RagAnswer(cleaned, Declined: false, sources);
        if (cache is { Enabled: true })
        {
            // What a later hit shows the user again: the cited documents, recorded as cited.
            var shown = context.Sources.Where((_, i) => cited.Contains(i + 1)).Select(s => s.Record)
                .GroupBy(r => r.DocumentId, StringComparer.Ordinal)
                .Select(g => new TouchedDocument(g.Key, g.First().Title, g.First().Sensitivity, g.First().ContainsPhi, AccessKind.Cited, [.. g.Select(r => r.ChunkId).Distinct(StringComparer.Ordinal)]))
                .ToList();
            await cache.SetAsync(question, caller, answer, shown, cancellationToken).ConfigureAwait(false);
        }

        return answer;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Answered {UserId} from the answer cache (similarity {Similarity:0.000}).")]
    private static partial void LogCacheHit(ILogger logger, string userId, double similarity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Declined for {UserId}: {Reason}.")]
    private static partial void LogDeclined(ILogger logger, string userId, string reason);
}
