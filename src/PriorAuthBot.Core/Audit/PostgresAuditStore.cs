using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;

namespace PriorAuthBot.Core.Audit;

/// <summary>
/// The audit log in PostgreSQL (chapter 13), in the same server as the vector store but its own
/// table, with its own grants. The table refuses updates, deletes inside the retention period, and
/// truncation (audit-log.sql). Appends from every API instance are serialized with a transaction-level
/// advisory lock, so the chain has one order however many instances write to it.
/// </summary>
public sealed class PostgresAuditStore(NpgsqlDataSource dataSource, bool createSchema = false) : IAuditStore
{
    private Task? _ready;

    // Any constant works, as long as every writer uses the same one.
    private const long AppendLock = 0x5052_4941_5544_4954; // "PRIAUDIT"

    public static string SchemaSql { get; } = LoadSchema();

    /// <summary>
    /// Creates the table and its triggers if they are missing. For a developer's database and the
    /// tests; in production a migration runs audit-log.sql as the schema owner, and the API's role
    /// only has INSERT and SELECT.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        // Several instances starting at once would deadlock on the DDL, and so would DDL racing an
        // append from an instance that is already running. The same lock as appends, in one transaction.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = AppendLock });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = new NpgsqlCommand(SchemaSql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuditRecord> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = AppendLock });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long sequence = 0;
        var previousHash = AuditChain.Genesis;
        await using (var last = new NpgsqlCommand("SELECT sequence, hash FROM priorauthbot_audit ORDER BY sequence DESC LIMIT 1", connection, transaction))
        await using (var reader = await last.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sequence = reader.GetInt64(0);
                previousHash = reader.GetString(1);
            }
        }

        var sealedRecord = AuditChain.Seal(record, sequence + 1, previousHash);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO priorauthbot_audit (sequence, audit_id, recorded_at, user_id, operation, record, previous_hash, hash) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
            connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.Sequence });
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.Id });
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.At.ToUniversalTime() });
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.UserId });
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.Operation });
            insert.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(sealedRecord, AuditChain.Json) });
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.PreviousHash });
            insert.Parameters.Add(new NpgsqlParameter { Value = sealedRecord.Hash });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sealedRecord;
    }

    public async IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = dataSource.CreateCommand(
            """
            SELECT record FROM priorauthbot_audit
            WHERE ($1::text IS NULL OR user_id = $1)
              AND ($2::text IS NULL OR audit_id = $2)
              AND ($3::timestamptz IS NULL OR recorded_at >= $3)
              AND ($4::timestamptz IS NULL OR recorded_at < $4)
            ORDER BY sequence
            LIMIT $5
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)query.UserId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)query.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)query.From?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)query.To?.ToUniversalTime() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = query.Limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return JsonSerializer.Deserialize<AuditRecord>(reader.GetString(0), AuditChain.Json)
                ?? throw new InvalidOperationException("An audit record is empty.");
        }
    }

    // Once per instance, shared by every caller. A failed attempt is retried by the next call.
    private Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!createSchema)
        {
            return Task.CompletedTask;
        }

        var ready = Volatile.Read(ref _ready);
        if (ready is null || ready.IsFaulted || ready.IsCanceled)
        {
            var started = EnsureSchemaAsync(CancellationToken.None);
            ready = Interlocked.CompareExchange(ref _ready, started, ready) == ready ? started : _ready!;
        }

        return ready.WaitAsync(cancellationToken);
    }

    private static string LoadSchema()
    {
        using var stream = typeof(PostgresAuditStore).Assembly.GetManifestResourceStream("PriorAuthBot.Core.Audit.audit-log.sql")
            ?? throw new InvalidOperationException("The audit-log.sql resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
