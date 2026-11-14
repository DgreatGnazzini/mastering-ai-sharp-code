using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace PriorAuthBot.Core.Search;

/// <summary>
/// Splits text into the terms keyword search matches on (chapter 8). The rules are chosen for the
/// strings a health plan's staff type: billing codes (<c>72148</c>, <c>E0601</c>) and policy numbers
/// (<c>HR-104</c>) stay whole, so <c>HR-104</c> is one term as well as <c>hr</c> and <c>104</c>.
/// Common English words are dropped, and a few plural and tense endings are stripped so that
/// "screens" matches "screen". PostgreSQL's full-text search does a fuller job of the same steps.
/// </summary>
public static partial class KeywordTokenizer
{
    private static readonly FrozenSet<string> StopWords = new[]
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "any", "are", "as", "at", "be", "been", "before",
        "being", "but", "by", "can", "could", "did", "do", "does", "doing", "for", "from", "get", "got", "had",
        "has", "have", "how", "i", "if", "in", "into", "is", "it", "its", "me", "might", "more", "must", "my",
        "no", "not", "of", "on", "or", "our", "say", "says", "should", "so", "than", "that", "the", "their",
        "them", "then", "there", "these", "they", "this", "to", "up", "us", "was", "we", "were", "what", "when",
        "where", "which", "who", "why", "will", "with", "would", "you", "your",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The terms in <paramref name="text"/>, in order, with repeats.</summary>
    public static List<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = new List<string>();
        foreach (Match match in Word().Matches(text.ToLowerInvariant()))
        {
            var word = match.Value;
            if (word.Contains('-', StringComparison.Ordinal))
            {
                // Keep the code whole, and its parts, so "HR-104" matches "HR-104" exactly and "104" too.
                terms.Add(word);
                foreach (var part in word.Split('-'))
                {
                    AddTerm(terms, part);
                }
            }
            else
            {
                AddTerm(terms, word);
            }
        }

        return terms;
    }

    /// <summary>The distinct terms of a query, which is what coverage is measured against.</summary>
    public static IReadOnlyList<string> QueryTerms(string query) => Tokenize(query).Distinct(StringComparer.Ordinal).ToList();

    private static void AddTerm(List<string> terms, string word)
    {
        if (word.Length == 0 || StopWords.Contains(word))
        {
            return;
        }

        terms.Add(Stem(word));
    }

    /// <summary>A deliberately small stemmer: plurals and "-ed"/"-ing" on longer words. Codes are left alone.</summary>
    internal static string Stem(string word)
    {
        if (word.Any(char.IsDigit))
        {
            return word;
        }

        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            return word[..^3];
        }

        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            return word[..^3] + "y";
        }

        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal))
        {
            return word[..^1];
        }

        return word;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
