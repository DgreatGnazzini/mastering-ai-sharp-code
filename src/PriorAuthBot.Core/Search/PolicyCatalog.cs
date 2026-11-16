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
}

/// <summary>
/// Semantic search over the policy catalog (chapter 5): about 200 summaries, embedded once, held in
/// memory, and searched with <see cref="SemanticIndex{T}"/>. Every entry is readable by all staff,
/// so there is no access filter here; chapter 6 moves documents with access lists into a vector store.
/// </summary>
public sealed class PolicyCatalog(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, CatalogOptions options) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private SemanticIndex<CatalogEntry>? _index;

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
        var requests = 0;

        foreach (var batch in entries.Chunk(options.BatchSize))
        {
            var inputs = batch.Select(e => options.DocumentPrefix + EmbeddingInput(e)).ToList();
            var embeddings = await embeddingGenerator.GenerateAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);
            requests++;
            for (var i = 0; i < batch.Length; i++)
            {
                index.Add(batch[i], embeddings[i].Vector);
            }
        }

        _index = index;
        LastBuild = new CatalogBuildStats(entries.Count, requests, watch.Elapsed);
        return LastBuild;
    }

    public async Task<IReadOnlyList<CatalogHit>> SearchAsync(string query, int? top = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var index = _index ?? throw new InvalidOperationException("The catalog has not been built. Call BuildAsync first.");
        var vector = await embeddingGenerator.GenerateVectorAsync(options.QueryPrefix + query, cancellationToken: cancellationToken).ConfigureAwait(false);
        return index.Search(vector.Span, top ?? options.Top)
            .Select(r => new CatalogHit(r.Item.Id, r.Item.Policy, r.Item.Title, r.Item.Department, r.Item.Summary, Math.Round(r.Score, 3)))
            .ToList();
    }

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
