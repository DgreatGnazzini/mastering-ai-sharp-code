namespace PriorAuthBot.Core.Audit;

/// <summary>A filter on the audit log. Every field is optional; the result is in sequence order.</summary>
public sealed record AuditQuery(string? UserId = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Id = null, int Limit = 10_000);

/// <summary>
/// Where audit records are kept (chapter 13). There is no update and no delete: a store can only
/// append and read. Removing records at the end of the retention period is a separate, privileged
/// job (scripts/audit-log.sql), never something the API can do.
/// </summary>
public interface IAuditStore
{
    /// <summary>Appends a record and returns it sealed into the chain. Concurrent appends are serialized.</summary>
    Task<AuditRecord> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default);
}

/// <summary>For tests and a developer's machine only: Program.cs refuses it outside Development.</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditRecord> _records = [];
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public Task<AuditRecord> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var previous = _records.Count == 0 ? null : _records[^1];
            var sealedRecord = AuditChain.Seal(record, (previous?.Sequence ?? 0) + 1, previous?.Hash ?? AuditChain.Genesis);
            _records.Add(sealedRecord);
            return Task.FromResult(sealedRecord);
        }
    }

    public async IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<AuditRecord> snapshot;
        lock (_gate)
        {
            snapshot = [.. _records];
        }

        foreach (var record in snapshot.Where(r => Matches(r, query)).Take(query.Limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Test hook: what an attacker with write access to the storage could do. The chain must catch it.</summary>
    internal void Tamper(long sequence, Func<AuditRecord, AuditRecord> change)
    {
        lock (_gate)
        {
            var i = _records.FindIndex(r => r.Sequence == sequence);
            _records[i] = change(_records[i]);
        }
    }

    internal void Remove(long sequence)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => r.Sequence == sequence);
        }
    }

    internal static bool Matches(AuditRecord record, AuditQuery query) =>
        (query.UserId is null || record.UserId == query.UserId)
        && (query.Id is null || record.Id == query.Id)
        && (query.From is null || record.At >= query.From)
        && (query.To is null || record.At < query.To);
}
