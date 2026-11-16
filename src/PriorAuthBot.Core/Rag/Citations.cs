using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Rag;

/// <summary>A sentence of an answer and the source numbers it cites.</summary>
public sealed record CitedSentence(string Text, IReadOnlyList<int> Sources);

/// <summary>
/// Turns the model's [n] markers into structured citations (chapter 8). The model is asked to cite
/// with numbers because numbers are short and hard to get subtly wrong; this class checks them.
/// A marker for a source that does not exist is removed rather than shown, because a citation that
/// points nowhere is worse than none.
/// </summary>
public static partial class Citations
{
    /// <summary>The source numbers cited in <paramref name="answer"/>, and the answer with any invalid markers removed.</summary>
    public static (string Answer, IReadOnlySet<int> Cited, int Removed) Resolve(string answer, int sourceCount)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var cited = new SortedSet<int>();
        var removed = 0;
        var cleaned = Marker().Replace(answer, match =>
        {
            var valid = new List<int>();
            foreach (var part in match.Groups["n"].Value.Split(','))
            {
                if (int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= sourceCount)
                {
                    valid.Add(n);
                    cited.Add(n);
                }
                else
                {
                    removed++;
                }
            }

            return valid.Count == 0 ? string.Empty : "[" + string.Join("][", valid) + "]";
        });

        // Removing a marker can leave "word ." behind.
        cleaned = SpaceBeforePunctuation().Replace(cleaned, "$1");
        return (cleaned.Trim(), cited, removed);
    }

    /// <summary>Splits an answer into sentences with the sources each one cites, for citation checks.</summary>
    public static IReadOnlyList<CitedSentence> Sentences(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var sentences = new List<CitedSentence>();
        foreach (var raw in SentenceEnd().Split(answer.ReplaceLineEndings(" ")))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0)
            {
                continue;
            }

            var numbers = Marker().Matches(sentence)
                .SelectMany(m => m.Groups["n"].Value.Split(','))
                .Select(p => int.TryParse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .Where(n => n > 0)
                .Distinct()
                .ToList();
            var text = SpaceBeforePunctuation().Replace(Marker().Replace(sentence, string.Empty), "$1").Trim();
            if (text.Any(char.IsLetterOrDigit))
            {
                sentences.Add(new CitedSentence(text, numbers));
            }
        }

        return sentences;
    }

    /// <summary>The structured citation for source <paramref name="number"/>.</summary>
    public static SourceReference Reference(int number, ChunkRecord record, bool cited)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new SourceReference(number, record.DocumentId, record.Title, record.PolicyNumber, record.Section,
            record.ChunkId, record.Page, Link(record), cited);
    }

    /// <summary>
    /// <c>/documents/{id}#page-3</c> for a paged document, <c>/documents/{id}#approval-criteria</c>
    /// for a section, so the link lands where the cited text is.
    /// </summary>
    public static string Link(ChunkRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = "/documents/" + Uri.EscapeDataString(record.DocumentId);
        if (record.Page is { } page)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{path}#page-{page}");
        }

        var leaf = record.Section.Split('>').Last().Trim();
        return leaf.Length == 0 ? path : $"{path}#{Slug(leaf)}";
    }

    /// <summary>A heading as a URL fragment: lowercase, letters and digits, words joined by dashes.</summary>
    public static string Slug(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        var builder = new StringBuilder(heading.Length);
        foreach (var c in heading.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    [GeneratedRegex(@"\[(?<n>\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\s+([.,;:!?])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"(?<=[.!?](?:\s*\[\d+(?:\s*,\s*\d+)*\])*)\s+(?=[A-Z0-9\[])")]
    private static partial Regex SentenceEnd();
}
