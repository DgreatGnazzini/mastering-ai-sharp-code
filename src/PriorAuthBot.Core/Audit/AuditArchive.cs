using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Core.Audit;

/// <summary>Where the audit log's second copy and its anchors go (chapter 14).</summary>
public sealed class AuditArchiveOptions
{
    /// <summary><c>None</c>, or <c>Blob</c>: a container in Azure Blob Storage with a time-based immutability policy.</summary>
    public string Provider { get; set; } = None;

    /// <summary>The container, such as <c>https://stddaudit.blob.core.windows.net/audit-archive</c>. Sign-in is the managed identity.</summary>
    public Uri? ContainerUri { get; set; }

    /// <summary>How often new records are copied and anchored.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The most records in one archive file.</summary>
    public int MaxRecordsPerBatch { get; set; } = 5_000;

    public const string None = "None";
    public const string Blob = "Blob";
}

/// <summary>
/// The latest point of the hash chain written outside the database: the last archived record's
/// sequence number and hash. Anyone who rewrites the table afterwards, even with full database
/// rights and a recomputed chain, cannot make the chain match an anchor they cannot change.
/// </summary>
public sealed record AuditAnchor(long Sequence, string Hash, long FirstSequence, int Records, DateTimeOffset RecordedAt, DateTimeOffset ArchivedAt);

/// <summary>A write-once store for archived records and anchors.</summary>
public interface IAuditArchive
{
    Task<AuditAnchor?> LatestAnchorAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the records, then the anchor. Neither may already exist.</summary>
    Task WriteAsync(IReadOnlyList<AuditRecord> records, AuditAnchor anchor, CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure Blob Storage. The container has a time-based retention policy (infra/modules/storage.bicep),
/// so once a blob is written, nobody can change or delete it until the retention period ends. Once the
/// policy is locked, that includes the subscription's owners. Names sort by sequence number.
/// </summary>
public sealed class BlobAuditArchive(BlobContainerClient container) : IAuditArchive
{
    private const string AnchorPrefix = "anchors/";

    public async Task<AuditAnchor?> LatestAnchorAsync(CancellationToken cancellationToken = default)
    {
        string? latest = null;
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Prefix = AnchorPrefix }, cancellationToken).ConfigureAwait(false))
        {
            if (latest is null || string.CompareOrdinal(blob.Name, latest) > 0)
            {
                latest = blob.Name;
            }
        }

        if (latest is null)
        {
            return null;
        }

        var content = await container.GetBlobClient(latest).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return content.Value.Content.ToObjectFromJson<AuditAnchor>(AuditChain.Json);
    }

    public async Task WriteAsync(IReadOnlyList<AuditRecord> records, AuditAnchor anchor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(anchor);

        // overwrite: false sends If-None-Match: *, so a second writer for the same range fails instead of replacing.
        await container.GetBlobClient(AuditArchive.RecordsName(anchor))
            .UploadAsync(BinaryData.FromString(AuditArchive.ToJsonLines(records)), overwrite: false, cancellationToken).ConfigureAwait(false);
        await container.GetBlobClient(AuditArchive.AnchorName(anchor))
            .UploadAsync(BinaryData.FromObjectAsJson(anchor, AuditChain.Json), overwrite: false, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>For tests: a write-once dictionary.</summary>
public sealed class InMemoryAuditArchive : IAuditArchive
{
    private readonly Dictionary<string, string> _blobs = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyDictionary<string, string> Blobs
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_blobs, StringComparer.Ordinal);
            }
        }
    }

    public Task<AuditAnchor?> LatestAnchorAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var latest = _blobs.Keys.Where(k => k.StartsWith("anchors/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).LastOrDefault();
            return Task.FromResult(latest is null ? null : JsonSerializer.Deserialize<AuditAnchor>(_blobs[latest], AuditChain.Json));
        }
    }

    public Task WriteAsync(IReadOnlyList<AuditRecord> records, AuditAnchor anchor, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var recordsName = AuditArchive.RecordsName(anchor);
            var anchorName = AuditArchive.AnchorName(anchor);
            if (_blobs.ContainsKey(recordsName) || _blobs.ContainsKey(anchorName))
            {
                throw new RequestFailedException(409, "The blob already exists.");
            }

            _blobs[recordsName] = AuditArchive.ToJsonLines(records);
            _blobs[anchorName] = JsonSerializer.Serialize(anchor, AuditChain.Json);
        }

        return Task.CompletedTask;
    }
}

/// <summary>What one archive pass did.</summary>
public sealed record AuditArchiveResult(int Archived, AuditAnchor? Anchor, string? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>
/// Copies new audit records to the archive and anchors the chain, every
/// <see cref="AuditArchiveOptions.Interval"/>. Each pass checks that the new records continue the
/// chain from the last anchor: if someone changed or removed a record that was already anchored, or
/// rebuilt the chain, the first new record no longer points at the anchored hash, and the pass
/// refuses to archive and logs a critical event for the on-call engineer and compliance.
/// </summary>
public sealed partial class AuditArchive(IAuditStore store, IAuditArchive archive, IOptions<AuditOptions> options, ILogger<AuditArchive> logger, TimeProvider? time = null)
    : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static string RecordsName(AuditAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return $"records/{anchor.FirstSequence:D12}-{anchor.Sequence:D12}.jsonl";
    }

    public static string AnchorName(AuditAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return $"anchors/{anchor.Sequence:D12}.json";
    }

    public static string ToJsonLines(IEnumerable<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var text = new StringBuilder();
        foreach (var record in records)
        {
            text.Append(JsonSerializer.Serialize(record, AuditChain.Json)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>One pass: read what is new since the last anchor, check it continues the chain, write it.</summary>
    public async Task<AuditArchiveResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Archive;
        var anchor = await archive.LatestAnchorAsync(cancellationToken).ConfigureAwait(false);

        // Records are timestamped before they are sequenced, so read a little before the anchor's time
        // and keep only the sequence numbers after it.
        var from = anchor?.RecordedAt.AddMinutes(-5);
        var records = new List<AuditRecord>();
        await foreach (var record in store.QueryAsync(new AuditQuery(From: from, Limit: int.MaxValue), cancellationToken).ConfigureAwait(false))
        {
            if (anchor is null || record.Sequence > anchor.Sequence)
            {
                records.Add(record);
            }
        }

        records.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        if (records.Count == 0)
        {
            return new AuditArchiveResult(0, anchor, null);
        }

        records = [.. records.Take(settings.MaxRecordsPerBatch)];
        if (anchor is not null && (records[0].Sequence != anchor.Sequence + 1 || records[0].PreviousHash != anchor.Hash))
        {
            var problem = $"Record {records[0].Sequence} does not continue the chain from the anchor at record {anchor.Sequence}: a record was removed or the chain was rewritten.";
            LogChainBroken(logger, problem);
            return new AuditArchiveResult(0, anchor, problem);
        }

        var verification = AuditChain.Verify(records);
        if (!verification.Valid)
        {
            LogChainBroken(logger, verification.Problem ?? "The chain does not verify.");
            return new AuditArchiveResult(0, anchor, verification.Problem);
        }

        var last = records[^1];
        var next = new AuditAnchor(last.Sequence, last.Hash, records[0].Sequence, records.Count, last.At, _time.GetUtcNow());
        await archive.WriteAsync(records, next, cancellationToken).ConfigureAwait(false);
        LogArchived(logger, records.Count, next.FirstSequence, next.Sequence);
        return new AuditArchiveResult(records.Count, next, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Archive.Interval, _time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RequestFailedException or HttpRequestException or Npgsql.NpgsqlException)
            {
                // The next pass picks up where this one stopped; nothing is lost while the archive is unreachable.
                LogArchiveFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 1310, Level = LogLevel.Information, Message = "Archived {Count} audit records ({First} to {Last}) and anchored the chain")]
    private static partial void LogArchived(ILogger logger, int count, long first, long last);

    [LoggerMessage(EventId = 1311, Level = LogLevel.Critical, Message = "Audit chain check failed before archiving: {Problem}")]
    private static partial void LogChainBroken(ILogger logger, string problem);

    [LoggerMessage(EventId = 1312, Level = LogLevel.Warning, Message = "Audit archive pass failed; it will be retried")]
    private static partial void LogArchiveFailed(ILogger logger, Exception exception);
}
