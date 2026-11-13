using System.Numerics.Tensors;

namespace PriorAuthBot.Core.Search;

public readonly record struct ScoredItem<T>(T Item, float Score);

/// <summary>
/// Nearest-neighbor search in plain C# (chapter 5): every vector in a list, compared with the query
/// by cosine similarity, best <c>top</c> kept in a small heap. This is exact, brute-force search.
/// For a few thousand items it takes well under a millisecond; past a few hundred thousand you want
/// the approximate indexes a vector database provides (chapter 6).
/// </summary>
public sealed class SemanticIndex<T>
{
    private readonly List<(T Item, ReadOnlyMemory<float> Vector)> _entries = [];

    public int Count => _entries.Count;

    public void Add(T item, ReadOnlyMemory<float> vector)
    {
        if (_entries.Count > 0 && vector.Length != _entries[0].Vector.Length)
        {
            throw new ArgumentException($"Expected {_entries[0].Vector.Length} dimensions, got {vector.Length}. Vectors from different models cannot be compared.", nameof(vector));
        }

        _entries.Add((item, vector));
    }

    public IReadOnlyList<ScoredItem<T>> Search(ReadOnlySpan<float> query, int top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);

        // A min-heap of the best so far: the root is the worst of the current top, so a better
        // score replaces it in O(log top) instead of sorting every score.
        var best = new PriorityQueue<int, float>(top + 1);
        for (var i = 0; i < _entries.Count; i++)
        {
            var score = TensorPrimitives.CosineSimilarity(query, _entries[i].Vector.Span);
            if (best.Count < top)
            {
                best.Enqueue(i, score);
            }
            else if (best.TryPeek(out _, out var worst) && score > worst)
            {
                best.DequeueEnqueue(i, score);
            }
        }

        var results = new ScoredItem<T>[best.Count];
        for (var slot = results.Length - 1; slot >= 0; slot--)
        {
            best.TryDequeue(out var index, out var score);
            results[slot] = new ScoredItem<T>(_entries[index].Item, score);
        }

        return results;
    }
}
