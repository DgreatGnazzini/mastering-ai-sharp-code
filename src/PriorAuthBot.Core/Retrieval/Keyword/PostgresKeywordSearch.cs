using Microsoft.Extensions.VectorData;
using Npgsql;
using NpgsqlTypes;

namespace PriorAuthBot.Core.Retrieval.Keyword;

/// <summary>
/// Keyword search in PostgreSQL's built-in full-text search (chapter 8), against the same table the
/// pgvector connector writes. The connector creates a GIN index on
/// <c>to_tsvector('english', "SearchText")</c> because <see cref="ChunkRecord.SearchText"/> is marked
/// full-text indexed, and this query uses exactly that expression so the index applies.
/// </summary>
/// <remarks>
/// Two choices differ from the connector's own <c>HybridSearchAsync</c>. It uses
/// <c>plainto_tsquery</c>, which requires every word of the question to appear in the chunk;
/// this turns the same query into an OR, so "Is 27447 covered?" matches a chunk that says 27447
/// without the word "covered". And it returns the keyword ranking alone, so PriorAuthBot can gate
/// and fuse it in C# the same way for every store. <c>ts_rank_cd</c> is not BM25: it rewards how
/// often and how close together the terms occur, but knows nothing of how rare a term is.
/// </remarks>
public sealed class PostgresKeywordSearch(NpgsqlDataSource dataSource, VectorStoreCollection<Guid, ChunkRecord> collection, string tableName) : IKeywordSearch
{
    public string Name => "PostgreSQL full-text search";

    public async Task<IReadOnlyList<ChunkRecord>> SearchAsync(string query, IReadOnlyList<string> groups, int top, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            return [];
        }

        // The table name is a constant from configuration code, never user input; values are parameters.
        var sql = $"""
            WITH q AS (SELECT replace(plainto_tsquery('english', $1)::text, ' & ', ' | ')::tsquery AS query)
            SELECT c."Key"
            FROM "{tableName}" c, q
            WHERE c."AllowedGroups" && $2
              AND to_tsvector('english', c."SearchText") @@ q.query
            ORDER BY ts_rank_cd(to_tsvector('english', c."SearchText"), q.query) DESC, c."Key"
            LIMIT $3
            """;

        var keys = new List<Guid>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = query });
            command.Parameters.Add(new NpgsqlParameter { Value = groups.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = top });
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    keys.Add(reader.GetGuid(0));
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // Nothing ingested yet.
                return [];
            }
        }

        if (keys.Count == 0)
        {
            return [];
        }

        // Read the records through the connector, so the mapping lives in one place, then restore the ranking.
        var records = new Dictionary<Guid, ChunkRecord>();
        await foreach (var record in collection.GetAsync(keys, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            records[record.Key] = record;
        }

        return keys.Where(records.ContainsKey).Select(k => records[k]).ToList();
    }
}
