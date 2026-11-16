using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Caching;

/// <summary>A cached answer, with the documents it was built from, so a cache hit is audited like the original.</summary>
public sealed record CachedAnswer(RagAnswer Answer, IReadOnlyList<TouchedDocument> Documents, double Similarity, string Partition);

/// <summary>
/// A semantic cache for grounded answers (chapter 13). A question is embedded and compared with the
/// questions already answered <em>in the caller's partition</em>; a close enough match returns the
/// stored answer with no retrieval and no model call. Lookups never cross partitions: there is no
/// code path that compares a question with another permission set's entries.
/// </summary>
/// <remarks>
/// In memory, per API instance. A shared cache (Redis with vector search, or a pgvector table) must
/// keep the partition in the key and in every query's filter, exactly like the vector store's
/// group filter in chapter 6.
/// </remarks>
public sealed class SemanticAnswerCache(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IOptionsMonitor<AnswerCacheOptions> options,
    IOptions<AiOptions> ai,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Partition> _partitions = new(StringComparer.Ordinal);
    private long _generation;

    public bool Enabled => options.CurrentValue.Enabled;

    /// <summary>Bumped when documents change. Old partitions are no longer reachable and are dropped.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public int Count => _partitions.Values.Sum(p => p.Count);

    /// <summary>Called after ingestion changes any document or ACL: every cached answer may be stale.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _partitions.Clear();
    }

    public string PartitionFor(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return PermissionPartition.For(caller.Groups, Model, PromptLibrary.GroundedAnswer, Generation);
    }

    public async Task<CachedAnswer?> GetAsync(string question, CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(caller);
        if (!Enabled || caller.Groups.Count == 0)
        {
            return null;
        }

        var key = PartitionFor(caller);
        CachedAnswer? hit = null;
        if (_partitions.TryGetValue(key, out var partition))
        {
            var vector = await EmbedAsync(question, cancellationToken).ConfigureAwait(false);
            hit = partition.Find(Normalize(question), vector, options.CurrentValue.SimilarityThreshold, _time.GetUtcNow(), key);
        }

        PriorAuthBotTelemetry.AnswerCacheLookups.Add(1, new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.CacheResult, hit is null ? "miss" : "hit"));
        System.Diagnostics.Activity.Current?.SetTag(PriorAuthBotTelemetry.Tags.CacheResult, hit is null ? "miss" : "hit");
        return hit;
    }

    /// <summary>
    /// Stores an answer for the caller's partition. Declines are not stored (they cost no model call),
    /// and neither is anything the caller's own identity shaped beyond their groups: only grounded
    /// answers from <see cref="RagAnswerService"/> come here, never tool or agent replies.
    /// </summary>
    public async Task SetAsync(string question, CallerIdentity caller, RagAnswer answer, IReadOnlyList<TouchedDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(documents);
        if (!Enabled || answer.Declined || caller.Groups.Count == 0)
        {
            return;
        }

        var key = PartitionFor(caller);
        var vector = await EmbedAsync(question, cancellationToken).ConfigureAwait(false);
        var settings = options.CurrentValue;
        _partitions.GetOrAdd(key, _ => new Partition())
            .Add(new Entry(Normalize(question), vector, answer, documents, _time.GetUtcNow() + settings.TimeToLive), settings.MaxEntriesPerPartition);
    }

    /// <summary>Cosine similarity between two questions, as the cache sees it. The eval's threshold table uses this.</summary>
    public async Task<double> SimilarityAsync(string a, string b, CancellationToken cancellationToken = default)
    {
        var x = await EmbedAsync(a, cancellationToken).ConfigureAwait(false);
        var y = await EmbedAsync(b, cancellationToken).ConfigureAwait(false);
        return TensorPrimitives.CosineSimilarity(x, y);
    }

    private string Model => ai.Value.Provider switch
    {
        "Ollama" => "ollama:" + ai.Value.Ollama.ChatModel,
        "AzureOpenAI" => "azure:" + ai.Value.AzureOpenAI.ChatDeployment,
        "OpenAI" => "openai:" + ai.Value.OpenAI.ChatModel,
        var other => other,
    };

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
        (await embeddings.GenerateVectorAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false)).ToArray();

    private static string Normalize(string question) =>
        string.Join(' ', question.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('?', '.', '!');

    private sealed record Entry(string Text, float[] Vector, RagAnswer Answer, IReadOnlyList<TouchedDocument> Documents, DateTimeOffset Expires);

    private sealed class Partition
    {
        private readonly LinkedList<Entry> _entries = new();
        private readonly Lock _gate = new();

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public void Add(Entry entry, int max)
        {
            lock (_gate)
            {
                _entries.AddFirst(entry);
                while (_entries.Count > max)
                {
                    _entries.RemoveLast();
                }
            }
        }

        public CachedAnswer? Find(string text, float[] vector, double threshold, DateTimeOffset now, string key)
        {
            lock (_gate)
            {
                Entry? best = null;
                var bestScore = double.MinValue;
                for (var node = _entries.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.Expires <= now)
                    {
                        _entries.Remove(node);
                    }
                    else
                    {
                        var score = node.Value.Text == text ? 1.0 : TensorPrimitives.CosineSimilarity(vector, node.Value.Vector);
                        if (score > bestScore)
                        {
                            (best, bestScore) = (node.Value, score);
                        }
                    }

                    node = next;
                }

                return best is not null && bestScore >= threshold
                    ? new CachedAnswer(best.Answer, best.Documents, bestScore, key)
                    : null;
            }
        }
    }
}
