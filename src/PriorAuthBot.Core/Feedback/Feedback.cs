using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Privacy;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Feedback;

/// <summary>A user's rating of one answer, tied to its audit record.</summary>
public sealed record FeedbackRecord(string AuditId, string UserId, bool Helpful, string? Comment, DateTimeOffset At);

/// <summary>
/// A golden-set case proposed from feedback (chapter 13). Nothing here goes into the golden set by
/// itself: a person reviews each one, writes the expected answer, and commits it.
/// </summary>
public sealed record GoldenCaseCandidate(
    string Id,
    IReadOnlyList<string> Groups,
    string Question,
    bool Helpful,
    IReadOnlyList<string> CitedDocuments,
    string? Answer,
    string? Comment,
    string Note);

/// <summary>What an export produced: the candidates, and how many were held back because they contain PHI.</summary>
public sealed record FeedbackExport(IReadOnlyList<GoldenCaseCandidate> Candidates, int HeldBackForPhi, int MissingAuditRecord);

public interface IFeedbackStore
{
    Task SaveAsync(FeedbackRecord feedback, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackRecord>> ListAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken = default);
}

/// <summary>Ratings in memory, one per answer (the latest wins). A durable store belongs next to the audit log.</summary>
public sealed class InMemoryFeedbackStore : IFeedbackStore
{
    private readonly ConcurrentDictionary<string, FeedbackRecord> _ratings = new(StringComparer.Ordinal);

    public Task SaveAsync(FeedbackRecord feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        _ratings[feedback.AuditId] = feedback;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeedbackRecord>> ListAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackRecord>>([.. _ratings.Values.Where(f => f.At >= since && f.At < until).OrderBy(f => f.At)]);
}

/// <summary>
/// Thumbs up and down on answers (chapter 13), and the export that turns them into golden-set
/// candidates. A rating names an audit record, not a question, so the rating holds no question text
/// and nobody can rate an answer they were not given.
/// </summary>
public sealed partial class FeedbackService(IFeedbackStore store, AuditLog audit, ILogger<FeedbackService> logger, TimeProvider? time = null)
{
    public const int MaxCommentLength = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Saves a rating. Returns false, and saves nothing, unless the audit record exists, belongs to the
    /// caller, and is an answer. The endpoint answers every false with the same 404.
    /// </summary>
    public async Task<bool> SubmitAsync(string auditId, CallerIdentity caller, bool helpful, string? comment, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditId);
        ArgumentNullException.ThrowIfNull(caller);
        AuditRecord? answered = null;
        await foreach (var record in audit.QueryAsync(new AuditQuery(Id: auditId, Limit: 1), cancellationToken).ConfigureAwait(false))
        {
            answered = record;
        }

        if (answered is null || answered.UserId != caller.UserId || answered.Operation != AnswerOperation || answered.Status != 200)
        {
            return false;
        }

        // A comment is free text from a user, so it can hold PHI. Store it redacted.
        var redacted = string.IsNullOrWhiteSpace(comment) ? null : PhiRedactor.Redact(comment.Trim()).Text;
        await store.SaveAsync(new FeedbackRecord(auditId, caller.UserId, helpful, redacted, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        PriorAuthBotTelemetry.Feedback.Add(1, new KeyValuePair<string, object?>("priorauthbot.feedback.rating", helpful ? "helpful" : "not_helpful"));
        LogRated(logger, auditId, helpful);
        return true;
    }

    /// <summary>The operation name of a grounded answer, as the audit log records it.</summary>
    public const string AnswerOperation = "POST /ask";

    /// <summary>
    /// Golden-set candidates from a period's ratings, joined with their audit records. A question is
    /// held back when the redactor finds an identifier in it: the golden set lives in git, and PHI must
    /// never get there. Unhelpful answers come first, because they are the cases the set is missing.
    /// </summary>
    public async Task<FeedbackExport> ExportCandidatesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var candidates = new List<GoldenCaseCandidate>();
        int heldBack = 0, missing = 0;
        foreach (var rating in await store.ListAsync(from, to, cancellationToken).ConfigureAwait(false))
        {
            AuditRecord? record = null;
            await foreach (var r in audit.QueryAsync(new AuditQuery(Id: rating.AuditId, Limit: 1), cancellationToken).ConfigureAwait(false))
            {
                record = r;
            }

            if (record?.Prompt is null)
            {
                missing++;
                continue;
            }

            if (PhiRedactor.Redact(record.Prompt).Total > 0)
            {
                heldBack++;
                continue;
            }

            var cited = record.Documents.Where(d => d.Access == AccessKind.Cited).Select(d => d.DocumentId).ToList();
            candidates.Add(new GoldenCaseCandidate(
                $"fb-{record.Id[..12]}",
                record.Groups,
                record.Prompt,
                rating.Helpful,
                cited,
                record.Response,
                rating.Comment,
                rating.Helpful
                    ? "Rated helpful: check the answer, then add it with expectedDocuments set to the cited documents."
                    : "Rated not helpful: find the right document and answer, then add it as a new case."));
        }

        return new FeedbackExport([.. candidates.OrderBy(c => c.Helpful)], heldBack, missing);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Feedback on answer {AuditId}: helpful = {Helpful}.")]
    private static partial void LogRated(ILogger logger, string auditId, bool helpful);
}
