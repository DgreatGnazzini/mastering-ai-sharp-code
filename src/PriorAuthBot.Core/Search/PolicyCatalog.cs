using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.Search;

/// <summary>One entry in Deductible Dynamics' policy catalog: a title and a one-paragraph summary.</summary>
public sealed record CatalogEntry(string Id, string Policy, string Title, string Department, string Summary);

public sealed record CatalogHit(string Id, string Policy, string Title, string Department, string Summary, double Score);

public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>The catalog file, relative to the content root unless absolute.</summary>
    public string Path { get; set; } = "../../samples/catalog/policy-catalog.json";

    /// <summary>Texts sent per embedding request. One request per text is the slow way (chapter 5).</summary>
    public int BatchSize { get; set; } = 32;

    public int Top { get; set; } = 5;

    /// <summary>
    /// Some embedding models are trained with task prefixes. nomic-embed-text expects
    /// <c>search_query: </c> on queries and <c>search_document: </c> on documents.
    /// </summary>
    public string QueryPrefix { get; set; } = string.Empty;

    public string DocumentPrefix { get; set; } = string.Empty;

    /// <summary>How <c>/catalog/search</c> ranks entries. Hybrid from chapter 8 on.</summary>
    public CatalogSearchMode Mode { get; set; } = CatalogSearchMode.Hybrid;

    /// <summary>How many candidates each side of a hybrid search contributes before fusion.</summary>
    public int CandidatesPerLeg { get; set; } = 20;
}

/// <summary>Chapter 5 searched by meaning only. Chapter 8 adds keywords, and both together.</summary>
public enum CatalogSearchMode
{
    Semantic,
    Keyword,
    Hybrid,
}

/// <summary>
/// Search over the policy catalog: about 200 summaries, embedded once, held in memory, and searched
/// by meaning with <see cref="SemanticIndex{T}"/> (chapter 5), by keyword with <see cref="Bm25Index{T}"/>,
/// or by both, fused with <see cref="ReciprocalRankFusion"/> (chapter 8). Every entry is readable by all staff,
/// so there is no access filter here; chapter 6 moves documents with access lists into a vector store.
/// </summary>
public sealed class PolicyCatalog(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, CatalogOptions options) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private SemanticIndex<CatalogEntry>? _index;
    private Bm25Index<CatalogEntry>? _keywords;

    /// <summary>What the last build cost: entries, embedding requests, and time.</summary>
    public CatalogBuildStats? LastBuild { get; private set; }

    public static IReadOnlyList<CatalogEntry> Load(string path) =>
        JsonSerializer.Deserialize<List<CatalogEntry>>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>
    /// The text that represents an entry. The policy number and title go first: the title carries a
    /// lot of meaning, and the number is there so a search for it has a chance (section 5.7 says how much).
    /// </summary>
    public static string EmbeddingInput(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{entry.Policy} {entry.Title}\n{entry.Summary}";
    }

    public async Task<CatalogBuildStats> BuildAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        var watch = Stopwatch.StartNew();
        var index = new SemanticIndex<CatalogEntry>();
        var keywords = new Bm25Index<CatalogEntry>();
        var requests = 0;

        foreach (var batch in entries.Chunk(options.BatchSize))
        {
            var inputs = batch.Select(e => options.DocumentPrefix + EmbeddingInput(e)).ToList();
            var embeddings = await embeddingGenerator.GenerateAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);
            requests++;
            for (var i = 0; i < batch.Length; i++)
            {
                index.Add(batch[i], embeddings[i].Vector);
                keywords.Add(batch[i], EmbeddingInput(batch[i]));
            }
        }

        _index = index;
        _keywords = keywords;
        LastBuild = new CatalogBuildStats(entries.Count, requests, watch.Elapsed);
        return LastBuild;
    }

    public async Task<IReadOnlyList<CatalogHit>> SearchAsync(string query, int? top = null, CancellationToken cancellationToken = default) =>
        await SearchAsync(query, options.Mode, top, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CatalogHit>> SearchAsync(string query, CatalogSearchMode mode, int? top = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var index = _index ?? throw new InvalidOperationException("The catalog has not been built. Call BuildAsync first.");
        var keywords = _keywords!;
        var count = top ?? options.Top;

        if (mode == CatalogSearchMode.Keyword)
        {
            return keywords.Search(query, count).Select(r => Hit(r.Item, r.Score)).ToList();
        }

        var vector = await embeddingGenerator.GenerateVectorAsync(options.QueryPrefix + query, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (mode == CatalogSearchMode.Semantic)
        {
            return index.Search(vector.Span, count).Select(r => Hit(r.Item, r.Score)).ToList();
        }

        // Hybrid: the best candidates by meaning and by keyword, merged by rank (section 8.3).
        var candidates = Math.Max(options.CandidatesPerLeg, count);
        IReadOnlyList<IReadOnlyList<CatalogEntry>> rankings =
        [
            index.Search(vector.Span, candidates).Select(r => r.Item).ToList(),
            keywords.Search(query, candidates).Select(r => r.Item).ToList(),
        ];
        return ReciprocalRankFusion.Fuse(rankings, e => e.Id)
            .Take(count)
            .Select(f => Hit(f.Item, f.Score))
            .ToList();
    }

    private static CatalogHit Hit(CatalogEntry e, double score) =>
        new(e.Id, e.Policy, e.Title, e.Department, e.Summary, Math.Round(score, 4));

    /// <summary>Builds the catalog from its file on first use. Later calls return at once.</summary>
    public async Task EnsureBuiltAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_index is not null)
        {
            return;
        }

        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is null)
            {
                await BuildAsync(Load(path), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _buildLock.Release();
        }
    }

    public void Dispose() => _buildLock.Dispose();
}

public sealed record CatalogBuildStats(int Entries, int Requests, TimeSpan Elapsed);
