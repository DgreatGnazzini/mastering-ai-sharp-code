namespace PriorAuthBot.Core.Search;

/// <summary>
/// Keyword search with BM25, in plain C# (chapter 8). BM25 scores a document higher the more often
/// it contains the query's terms (with diminishing returns, set by <see cref="K1"/>), the rarer those
/// terms are across the whole collection, and the shorter the document is (set by <see cref="B"/>).
/// A rare term like <c>72148</c> therefore outweighs a common one like "policy". This is the scoring
/// Azure AI Search and Elasticsearch use for full-text search.
/// </summary>
public sealed class Bm25Index<T>
{
    /// <summary>Term-frequency saturation. 1.2 is the usual default.</summary>
    public const double K1 = 1.2;

    /// <summary>Length normalization, from 0 (none) to 1 (full). 0.75 is the usual default.</summary>
    public const double B = 0.75;

    private readonly List<(T Item, Dictionary<string, int> TermCounts, int Length)> _documents = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private long _totalLength;

    public int Count => _documents.Count;

    public void Add(T item, string text)
    {
        var terms = KeywordTokenizer.Tokenize(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            counts[term] = counts.GetValueOrDefault(term) + 1;
        }

        foreach (var term in counts.Keys)
        {
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        }

        _documents.Add((item, counts, terms.Count));
        _totalLength += terms.Count;
    }

    /// <summary>The best <paramref name="top"/> items that contain at least one query term, best first.</summary>
    public IReadOnlyList<ScoredItem<T>> Search(string query, int top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        var queryTerms = KeywordTokenizer.QueryTerms(query);
        if (_documents.Count == 0 || queryTerms.Count == 0)
        {
            return [];
        }

        var averageLength = (double)_totalLength / _documents.Count;
        var scored = new List<ScoredItem<T>>();
        foreach (var (item, counts, length) in _documents)
        {
            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!counts.TryGetValue(term, out var frequency))
                {
                    continue;
                }

                score += Idf(term) * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * length / averageLength));
            }

            if (score > 0)
            {
                scored.Add(new ScoredItem<T>(item, (float)score));
            }
        }

        return scored.OrderByDescending(s => s.Score).Take(top).ToList();
    }

    /// <summary>Inverse document frequency, in the form Lucene uses, which never goes negative.</summary>
    private double Idf(string term)
    {
        var n = _documents.Count;
        var df = _documentFrequency.GetValueOrDefault(term);
        return Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
    }
}
