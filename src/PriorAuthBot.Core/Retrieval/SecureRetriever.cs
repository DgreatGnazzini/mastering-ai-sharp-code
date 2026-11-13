using System.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// Permission-aware vector search (chapters 6 and 7). The caller's groups become a filter inside
/// the vector query, so the store never returns a chunk the caller cannot see. Results are not
/// filtered after the fact, because post-filtering leaks through scores, counts, and timing, and
/// silently returns fewer results than asked for.
/// </summary>
public sealed partial class SecureRetriever(
    VectorStoreCollection<string, ChunkRecord> collection,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<RetrievalOptions> options,
    ILogger<SecureRetriever> logger)
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(caller);

        // No groups means no access. Never translate "no groups" into "no filter".
        if (caller.Groups.Count == 0)
        {
            LogNoGroups(logger, caller.UserId);
            return [];
        }

        var settings = options.Value;
        var groups = caller.Groups.ToList();
        var vector = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);

        var searchOptions = new VectorSearchOptions<ChunkRecord>
        {
            // Translated by the connector: "allowed_groups && @groups" in PostgreSQL,
            // a payload filter in Qdrant, an OData filter in Azure AI Search.
            Filter = r => r.AllowedGroups.Any(g => groups.Contains(g)),
            ScoreThreshold = settings.MinScore,
        };

        var results = new List<RetrievedChunk>();
        await foreach (var result in collection.SearchAsync(vector, settings.Top, searchOptions, cancellationToken).ConfigureAwait(false))
        {
            // Defense in depth: the filter already did this. If it ever fails, stop loudly.
            if (!caller.IsInAnyGroup(result.Record.AllowedGroups))
            {
                throw new SecurityException($"Vector store returned chunk {result.Record.Key}, which {caller.UserId} may not see.");
            }

            results.Add(new RetrievedChunk(result.Record, result.Score ?? 0));
        }

        LogSearch(logger, caller.UserId, results.Count);
        return results;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Caller {UserId} has no groups; returning no results.")]
    private static partial void LogNoGroups(ILogger logger, string userId);

    // Log identifiers and counts only. Question text can contain PHI (chapter 13).
    [LoggerMessage(Level = LogLevel.Information, Message = "Search for {UserId} returned {Count} chunks.")]
    private static partial void LogSearch(ILogger logger, string userId, int count);
}
