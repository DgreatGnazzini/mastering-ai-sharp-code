using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Audit;

/// <summary>
/// One entry in PriorAuthBot's audit log (chapter 13): who asked, with which permissions, which
/// documents the request touched, what went back, and what it cost. Unlike a trace, it holds the
/// question and the answer, because "show me what this user saw" is the question an auditor asks.
/// That is why it lives in its own store, readable only by the audit reader role.
/// </summary>
/// <remarks>
/// Records form a hash chain: each one's <see cref="Hash"/> covers its content and the previous
/// record's hash. Changing, removing, or reordering any record breaks every hash after it, which
/// <see cref="AuditChain.Verify"/> finds. The store makes the log append-only; the chain makes a
/// change detectable even by someone who got around the store.
/// </remarks>
public sealed record AuditRecord
{
    public long Sequence { get; init; }

    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string UserId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The permission set the request ran with, sorted.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>The endpoint, such as <c>POST /ask</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>The HTTP status the user got.</summary>
    public int Status { get; init; }

    /// <summary>Why the request failed, as an exception type name, such as <c>PromptRejectedException</c>. Never a message.</summary>
    public string? Error { get; init; }

    public string? Prompt { get; init; }

    public string? Response { get; init; }

    public IReadOnlyList<TouchedDocument> Documents { get; init; } = [];

    public bool AnswerCacheHit { get; init; }

    public int ModelCalls { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public decimal Cost { get; init; }

    /// <summary>The W3C trace the request ran in, to find its spans. The trace has the audit ID; the audit record has the trace ID.</summary>
    public string? TraceId { get; init; }

    public string PreviousHash { get; init; } = string.Empty;

    public string Hash { get; init; } = string.Empty;
}

/// <summary>The result of checking the chain.</summary>
/// <param name="FirstSequence">The oldest record checked. Records before it were purged at the end of their retention period.</param>
public sealed record AuditVerification(bool Valid, long Count, long FirstSequence, long LastSequence, string? Problem);

/// <summary>Hashing and checking the audit chain.</summary>
public static class AuditChain
{
    /// <summary>The previous hash of the first record ever written.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>The one serialization the hash is computed over: fixed property order, no indentation.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Gives a record its place in the chain: its sequence number, the previous hash, and its own hash.</summary>
    public static AuditRecord Seal(AuditRecord record, long sequence, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(previousHash);
        var placed = record with { Sequence = sequence, PreviousHash = previousHash, Hash = string.Empty };
        return placed with { Hash = ComputeHash(placed) };
    }

    public static string ComputeHash(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var content = JsonSerializer.Serialize(record with { Hash = string.Empty }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    /// Checks records in sequence order: each hash matches its content, each record points at the one
    /// before it, and no sequence number is missing. The first record may point at a purged one.
    /// </summary>
    public static AuditVerification Verify(IEnumerable<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        AuditRecord? previous = null;
        long count = 0;
        long first = 0;
        foreach (var record in records)
        {
            count++;
            if (previous is null)
            {
                first = record.Sequence;
            }
            else if (record.Sequence != previous.Sequence + 1)
            {
                return new(false, count, first, record.Sequence, $"Record {previous.Sequence + 1} is missing (found {record.Sequence} after {previous.Sequence}).");
            }
            else if (record.PreviousHash != previous.Hash)
            {
                return new(false, count, first, record.Sequence, $"Record {record.Sequence} does not point at record {previous.Sequence}.");
            }

            if (ComputeHash(record) != record.Hash)
            {
                return new(false, count, first, record.Sequence, $"Record {record.Sequence} was changed after it was written.");
            }

            previous = record;
        }

        return new(true, count, first, previous?.Sequence ?? 0, null);
    }
}
