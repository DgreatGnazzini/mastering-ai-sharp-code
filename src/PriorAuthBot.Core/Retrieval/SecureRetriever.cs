using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Retrieval.Keyword;
using PriorAuthBot.Core.Retrieval.Reranking;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Search;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// Permission-aware retrieval (chapters 6 to 8). The caller's groups become a filter inside every
/// query, vector and keyword alike, so no store ever returns a chunk the caller cannot see. Results
/// are not filtered after the fact, because post-filtering leaks through scores, counts, and timing,
/// and silently returns fewer results than asked for.
/// </summary>
/// <remarks>
/// In hybrid mode (chapter 8) a search runs in up to four steps: rewrite the question into a few
/// queries (optional), run vector and keyword search for each, fuse every ranking with reciprocal
/// rank fusion, then rerank the best candidates (optional). Only chunks that passed a relevance
/// gate on their own side reach fusion, so PriorAuthBot can still say "I don't know".
/// </remarks>
public sealed partial class SecureRetriever(
    VectorStoreCollection<Guid, ChunkRecord> collection,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<RetrievalOptions> options,
    ILogger<SecureRetriever> logger,
    IKeywordSearch? keywordSearch = null,
    IReranker? reranker = null,
    QueryRewriter? rewriter = null)
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(caller);

        // Chapter 13: one span per search, with counts only. The query text never goes on a span.
        using var activity = PriorAuthBotTelemetry.Source.StartActivity("priorauthbot.retrieve");
        var settings = options.Value;
        activity?.SetTag(PriorAuthBotTelemetry.Tags.RetrievalMode, settings.Mode.ToString());

        // No groups means no access. Never translate "no groups" into "no filter".
        if (caller.Groups.Count == 0)
        {
            LogNoGroups(logger, caller.UserId);
            return [];
        }

        var results = settings.Mode == RetrievalMode.Vector
            ? await VectorSearchAsync(query, caller, settings.Top, cancellationToken).ConfigureAwait(false)
            : await HybridSearchAsync(query, caller, settings, cancellationToken).ConfigureAwait(false);

        LogSearch(logger, caller.UserId, results.Count);
        Record(activity, results.Select(r => r.Record), caller, AccessKind.Retrieved);
        return results;
    }

    /// <summary>
    /// The chunks of one document the caller may see, in order, or null. Null means "not found"
    /// whether the document is missing or restricted, so the answer never confirms that a
    /// restricted document exists (section 8.6).
    /// </summary>
    public async Task<DocumentView?> GetDocumentAsync(string documentId, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Groups.Count == 0 || !await collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var groups = caller.Groups.ToList();
        var chunks = new List<ChunkRecord>();
        await foreach (var record in collection.GetAsync(r => r.DocumentId == documentId && r.AllowedGroups.Any(g => groups.Contains(g)), 1_000, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Check(record, caller);
            chunks.Add(record);
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        chunks.Sort((a, b) => a.ChunkIndex.CompareTo(b.ChunkIndex));
        Record(null, chunks, caller, AccessKind.Viewed);
        var first = chunks[0];
        return new DocumentView(first.DocumentId, first.Title, first.PolicyNumber,
            chunks.Select(c => new DocumentViewSection(Rag.Citations.Link(c).Split('#').ElementAtOrDefault(1) ?? string.Empty, c.Section, c.Page, c.Text)).ToList());
    }

    /// <summary>
    /// The document with this policy number, as the caller may see it, or null (chapter 10's
    /// <c>lookup_policy</c> tool). Like <see cref="GetDocumentAsync"/>, null means "not found"
    /// whether the policy does not exist or the caller may not read it.
    /// </summary>
    public async Task<DocumentView?> GetDocumentByPolicyNumberAsync(string policyNumber, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyNumber);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Groups.Count == 0 || !await collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The first chunk of each document carries its policy number. Find it inside the group
        // filter, then read the document through the same path as a citation link.
        var groups = caller.Groups.ToList();
        await foreach (var record in collection.GetAsync(r => r.PolicyNumber == policyNumber && r.ChunkIndex == 0 && r.AllowedGroups.Any(g => groups.Contains(g)), 1, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Check(record, caller);
            return await GetDocumentAsync(record.DocumentId, caller, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<List<RetrievedChunk>> HybridSearchAsync(string query, CallerIdentity caller, RetrievalOptions settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> queries = [query];
        if (settings.QueryRewrite.Enabled && rewriter is not null)
        {
            queries = await rewriter.ExpandAsync(query, settings.QueryRewrite.Count, cancellationToken).ConfigureAwait(false);
        }

        // One vector ranking and one keyword ranking per query, all fused together.
        var rankings = new List<IReadOnlyList<RetrievedChunk>>();
        foreach (var q in queries)
        {
            rankings.Add(await VectorSearchAsync(q, caller, settings.Candidates, cancellationToken).ConfigureAwait(false));
            rankings.Add(await KeywordSearchAsync(q, caller, settings, cancellationToken).ConfigureAwait(false));
        }

        var vectorScores = rankings.Where((_, i) => i % 2 == 0).SelectMany(r => r)
            .GroupBy(c => c.Record.Key).ToDictionary(g => g.Key, g => g.Max(c => c.VectorScore));
        var fused = ReciprocalRankFusion.Fuse(rankings, c => c.Record.Key, settings.RrfK)
            .Select(f => new RetrievedChunk(f.Item.Record, f.Score)
            {
                VectorScore = vectorScores.GetValueOrDefault(f.Item.Record.Key),
                KeywordRank = f.Ranks.Where((_, i) => i % 2 == 1).Min(),
            })
            .ToList();

        if (reranker is null || settings.Rerank.Provider == "None" || fused.Count == 0)
        {
            return fused.Take(settings.Top).ToList();
        }

        return await RerankAsync(query, fused, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<RetrievedChunk>> RerankAsync(string query, List<RetrievedChunk> fused, RetrievalOptions settings, CancellationToken cancellationToken)
    {
        // Rerank against the original question, not the rewrites: it is what the user asked.
        var candidates = fused.Take(settings.Rerank.Candidates).ToList();
        IReadOnlyList<double> scores;
        try
        {
            scores = await reranker!.ScoreAsync(query, candidates.Select(c => c.Record.SearchText).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (AI.StructuredOutputException)
        {
            // Reranking is an optimization. If the LLM reranker's reply is unusable, keep the fused order.
            LogRerankFailed(logger, reranker!.Name);
            return fused.Take(settings.Top).ToList();
        }

        var reranked = candidates
            .Select((c, i) => c with { Score = scores[i], RerankScore = scores[i] })
            .Where(c => settings.Rerank.MinScore is not { } min || c.Score >= min)
            .OrderByDescending(c => c.Score)
            .ToList();
        return reranked.Take(settings.Top).ToList();
    }

    private async Task<List<RetrievedChunk>> VectorSearchAsync(string query, CallerIdentity caller, int top, CancellationToken cancellationToken)
    {
        var groups = caller.Groups.ToList();
        var vector = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);

        var searchOptions = new VectorSearchOptions<ChunkRecord>
        {
            // Translated by the connector: "AllowedGroups" && @groups in PostgreSQL,
            // a payload filter in Qdrant, an OData filter in Azure AI Search.
            Filter = r => r.AllowedGroups.Any(g => groups.Contains(g)),
            ScoreThreshold = options.Value.MinScore,
        };

        var results = new List<RetrievedChunk>();
        await foreach (var result in collection.SearchAsync(vector, top, searchOptions, cancellationToken).ConfigureAwait(false))
        {
            Check(result.Record, caller);
            var score = result.Score ?? 0;
            results.Add(new RetrievedChunk(result.Record, score) { VectorScore = score });
        }

        return results;
    }

    private async Task<List<RetrievedChunk>> KeywordSearchAsync(string query, CallerIdentity caller, RetrievalOptions settings, CancellationToken cancellationToken)
    {
        if (keywordSearch is null)
        {
            return [];
        }

        var terms = KeywordTokenizer.QueryTerms(query);
        var results = new List<RetrievedChunk>();
        var matches = await keywordSearch.SearchAsync(query, caller.Groups, settings.Candidates, cancellationToken).ConfigureAwait(false);
        foreach (var record in matches)
        {
            Check(record, caller);

            // The keyword side's relevance gate: enough of the question's terms, not just one shared word.
            if (TermCoverage(terms, record.SearchText) < settings.MinTermCoverage)
            {
                continue;
            }

            results.Add(new RetrievedChunk(record, 0) { KeywordRank = results.Count + 1 });
        }

        return results;
    }

    /// <summary>The share of the query's distinct terms that appear in <paramref name="text"/>, from 0 to 1.</summary>
    public static double TermCoverage(IReadOnlyList<string> queryTerms, string text)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        if (queryTerms.Count == 0)
        {
            return 0;
        }

        var present = KeywordTokenizer.Tokenize(text).ToHashSet(StringComparer.Ordinal);
        return (double)queryTerms.Count(present.Contains) / queryTerms.Count;
    }

    /// <summary>Defense in depth: the filter already did this. If it ever fails, stop loudly.</summary>
    /// <summary>
    /// Chapter 13: every chunk that leaves the retriever is recorded on the request's audit context.
    /// This is the only way to documents, so nothing reaches a prompt, a search result, or a tool
    /// without an audit entry, whichever endpoint or agent asked.
    /// </summary>
    private static void Record(Activity? activity, IEnumerable<ChunkRecord> records, CallerIdentity caller, AccessKind access)
    {
        var list = records as IReadOnlyCollection<ChunkRecord> ?? [.. records];
        var request = AiRequestContext.Current;
        request?.SetCaller(caller);
        request?.RecordDocuments(list, access);
        if (activity is not null)
        {
            var documents = list.DistinctBy(r => r.DocumentId).ToList();
            activity.SetTag(PriorAuthBotTelemetry.Tags.ChunkCount, list.Count);
            activity.SetTag(PriorAuthBotTelemetry.Tags.DocumentCount, documents.Count);
            activity.SetTag(PriorAuthBotTelemetry.Tags.PhiDocumentCount, documents.Count(d => d.ContainsPhi));
        }
    }

    private static void Check(ChunkRecord record, CallerIdentity caller)
    {
        if (!caller.IsInAnyGroup(record.AllowedGroups))
        {
            throw new SecurityException($"Search returned chunk {record.ChunkId}, which {caller.UserId} may not see.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranker {Reranker} returned no usable scores; using the fused order.")]
    private static partial void LogRerankFailed(ILogger logger, string reranker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Caller {UserId} has no groups; returning no results.")]
    private static partial void LogNoGroups(ILogger logger, string userId);

    // Log identifiers and counts only. Question text can contain PHI (chapter 13).
    [LoggerMessage(Level = LogLevel.Information, Message = "Search for {UserId} returned {Count} chunks.")]
    private static partial void LogSearch(ILogger logger, string userId, int count);
}
