using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// A deterministic, offline <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> that hashes words
/// and word pairs into a fixed-size vector. It understands no meaning at all, which is the point:
/// tests and CI get repeatable vectors with no model, no network, and no cost. Never use it in
/// production. Real embeddings come from Ollama or OpenAI (chapter 5).
/// </summary>
public sealed class HashingEmbeddingGenerator(int dimensions = ChunkDimensions.Size) : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how",
        "i", "if", "in", "is", "it", "my", "of", "on", "or", "our", "the", "to", "what", "when",
        "which", "who", "with", "you", "your", "we", "this", "that", "must", "should", "may", "will",
    };

    private readonly EmbeddingGeneratorMetadata _metadata = new("hashing", defaultModelId: "hashing-v1", defaultModelDimensions: dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v)) { ModelId = "hashing-v1" }));
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata :
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private float[] Embed(string text)
    {
        var vector = new float[dimensions];
        var words = Tokenize(text);
        for (var i = 0; i < words.Count; i++)
        {
            Add(vector, words[i], 1.0f);
            if (i + 1 < words.Count)
            {
                Add(vector, words[i] + " " + words[i + 1], 0.5f);
            }
        }

        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }

        return vector;
    }

    private void Add(float[] vector, string term, float weight)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(term), hash);
        var bucket = (int)(BitConverter.ToUInt32(hash) % (uint)dimensions);
        var sign = (hash[4] & 1) == 0 ? 1f : -1f;
        vector[bucket] += sign * weight;
    }

    private static List<string> Tokenize(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                AddWord(words, current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            AddWord(words, current.ToString());
        }

        return words;
    }

    private static void AddWord(List<string> words, string word)
    {
        if (StopWords.Contains(word))
        {
            return;
        }

        // A crude stemmer is enough to match "screening" with "screen" and "claims" with "claim".
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            word = word[..^3];
        }
        else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            word = word[..^1];
        }

        words.Add(word);
    }
}
