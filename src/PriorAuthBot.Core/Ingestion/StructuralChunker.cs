using System.Text;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;

namespace PriorAuthBot.Core.Ingestion;

/// <summary>
/// Splits a document at its Markdown headings, so each chunk is one coherent section such as
/// "Carryover" or "Escalation" (chapter 7). A section larger than <see cref="ChunkingOptions.MaxTokens"/>
/// is split by paragraph, with overlap; small neighboring sections are never merged, because a
/// chunk that mixes two topics retrieves badly for both.
/// </summary>
public sealed class StructuralChunker(TokenCounter tokens, ChunkingOptions options) : IChunker
{
    public string Name => $"structural-{options.MaxTokens}";

    public IReadOnlyList<TextChunk> Chunk(SourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var chunks = new List<TextChunk>();
        foreach (var (section, body) in Sections(document.Text))
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            foreach (var piece in SplitSection(body))
            {
                chunks.Add(new TextChunk(chunks.Count, section, piece, tokens.Count(piece)));
            }
        }

        return chunks;
    }

    private static IEnumerable<(string Section, string Body)> Sections(string text)
    {
        var path = new List<(int Level, string Heading)>();
        var body = new StringBuilder();
        var section = string.Empty;

        foreach (var line in text.Split('\n'))
        {
            var level = HeadingLevel(line);
            if (level == 0)
            {
                // Always '\n', never Environment.NewLine: paragraph splitting below looks for "\n\n".
                body.Append(line).Append('\n');
                continue;
            }

            yield return (section, body.ToString().Trim());
            body.Clear();

            path.RemoveAll(p => p.Level >= level);
            path.Add((level, line[level..].Trim()));

            // The document title (H1) is stored on the record already, so it is left out of the path.
            section = string.Join(" > ", path.Where(p => p.Level > 1).Select(p => p.Heading));
        }

        yield return (section, body.ToString().Trim());
    }

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        return level is > 0 and <= 6 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private List<string> SplitSection(string body)
    {
        if (tokens.Count(body) <= options.MaxTokens)
        {
            return [body];
        }

        // Pack whole paragraphs up to the limit. Carry the last paragraph into the next chunk as
        // overlap when it is small enough, and fall back to token windows for a giant paragraph.
        var pieces = new List<string>();
        var current = new List<string>();
        var currentTokens = 0;
        foreach (var paragraph in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var paragraphTokens = tokens.Count(paragraph);
            if (paragraphTokens > options.MaxTokens)
            {
                Flush();
                pieces.AddRange(TokenWindows.Split(tokens, paragraph, options.MaxTokens, options.OverlapTokens));
                continue;
            }

            if (currentTokens + paragraphTokens > options.MaxTokens && current.Count > 0)
            {
                var carry = current[^1];
                Flush();
                if (tokens.Count(carry) <= options.OverlapTokens && tokens.Count(carry) + paragraphTokens <= options.MaxTokens)
                {
                    current.Add(carry);
                    currentTokens = tokens.Count(carry);
                }
            }

            current.Add(paragraph);
            currentTokens += paragraphTokens;
        }

        Flush();
        return pieces;

        void Flush()
        {
            if (current.Count > 0)
            {
                pieces.Add(string.Join("\n\n", current));
            }

            current.Clear();
            currentTokens = 0;
        }
    }
}
