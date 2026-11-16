using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Audit;

/// <summary>One document a user's requests touched over a period, for the auditor's report.</summary>
public sealed record DocumentAccessSummary(
    string DocumentId,
    string Title,
    string Sensitivity,
    bool ContainsPhi,
    int Requests,
    AccessKind StrongestAccess,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    IReadOnlyList<string> AuditIds);

/// <summary>
/// Writes and reads PriorAuthBot's audit log (chapter 13). The API writes one record per request,
/// from that request's <see cref="AiRequestContext"/>, before the response goes back.
/// </summary>
public sealed partial class AuditLog(IAuditStore store, IOptions<AuditOptions> options, ILogger<AuditLog> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AuditOptions Options => options.Value;

    /// <summary>Writes the record for a finished request. Throws when the store fails; the caller decides what that means.</summary>
    public async Task<AuditRecord> WriteAsync(AiRequestContext request, int status, Exception? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = options.Value.RecordContent;
        var record = new AuditRecord
        {
            Id = request.AuditId,
            At = _time.GetUtcNow(),
            UserId = request.UserId,
            DisplayName = request.DisplayName,
            Groups = request.Groups,
            Operation = request.Operation,
            Status = status,
            Error = error?.GetType().Name,
            Prompt = content ? request.Prompt : null,
            Response = content ? request.Response : null,
            Documents = request.Documents,
            AnswerCacheHit = request.AnswerCacheHit,
            ModelCalls = request.ModelCalls,
            InputTokens = request.InputTokens,
            OutputTokens = request.OutputTokens,
            Cost = request.Cost,
            Models = request.Models is { Count: > 0 } models ? models : null,
            TraceId = Activity.Current?.TraceId.ToHexString(),
        };

        try
        {
            // Not the request's token: a client that disconnects must not leave its request unaudited.
            var written = await store.AppendAsync(record, CancellationToken.None).ConfigureAwait(false);
            PriorAuthBotTelemetry.AuditRecords.Add(1, new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.Result, "written"));
            return written;
        }
        catch (Exception ex)
        {
            PriorAuthBotTelemetry.AuditRecords.Add(1, new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.Result, "failed"));
            LogWriteFailed(logger, ex, request.AuditId, request.Operation);
            throw;
        }
    }

    public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default) =>
        store.QueryAsync(query, cancellationToken);

    /// <summary>
    /// The security reviewer's first question: every document a user's requests touched in a period,
    /// optionally only the ones that contain PHI, with how many requests touched each and their audit IDs.
    /// </summary>
    public async Task<IReadOnlyList<DocumentAccessSummary>> DocumentsTouchedAsync(string userId, DateTimeOffset from, DateTimeOffset to, bool phiOnly, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var touches = new List<(AuditRecord Record, TouchedDocument Document)>();
        await foreach (var record in store.QueryAsync(new AuditQuery(UserId: userId, From: from, To: to, Limit: int.MaxValue), cancellationToken).ConfigureAwait(false))
        {
            touches.AddRange(record.Documents.Where(d => !phiOnly || d.ContainsPhi).Select(d => (record, d)));
        }

        return [.. touches
            .GroupBy(t => t.Document.DocumentId, StringComparer.Ordinal)
            .Select(g => new DocumentAccessSummary(
                g.Key,
                g.Last().Document.Title,
                g.Last().Document.Sensitivity,
                g.Any(t => t.Document.ContainsPhi),
                g.Count(),
                g.Max(t => t.Document.Access),
                g.Min(t => t.Record.At),
                g.Max(t => t.Record.At),
                [.. g.Select(t => t.Record.Id)]))
            .OrderBy(s => s.DocumentId, StringComparer.Ordinal)];
    }

    /// <summary>Walks the whole log and checks the hash chain.</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in store.QueryAsync(new AuditQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false))
        {
            records.Add(record);
        }

        return AuditChain.Verify(records);
    }

    [LoggerMessage(EventId = 1310, Level = LogLevel.Critical, Message = "Could not write audit record {AuditId} for {Operation}.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string auditId, string operation);
}
