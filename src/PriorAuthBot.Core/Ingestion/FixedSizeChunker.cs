using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;

namespace PriorAuthBot.Core.Ingestion;

/// <summary>
/// The baseline: cut the text every <see cref="ChunkingOptions.MaxTokens"/> tokens, with overlap,
/// ignoring structure. Easy to reason about, and the yardstick the structural chunker must beat.
/// </summary>
public sealed class FixedSizeChunker(TokenCounter tokens, ChunkingOptions options) : IChunker
{
    public string Name => $"fixed-{options.MaxTokens}";

    public IReadOnlyList<TextChunk> Chunk(SourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TokenWindows.Split(tokens, document.Text, options.MaxTokens, options.OverlapTokens)
            .Select((text, i) => new TextChunk(i, string.Empty, text, tokens.Count(text)))
            .ToList();
    }
}

internal static class TokenWindows
{
    /// <summary>Splits text into windows of at most <paramref name="maxTokens"/> tokens, breaking on whitespace.</summary>
    public static List<string> Split(TokenCounter tokens, string text, int maxTokens, int overlapTokens)
    {
        var windows = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > 0)
        {
            if (tokens.Count(remaining) <= maxTokens)
            {
                windows.Add(remaining);
                break;
            }

            var cut = tokens.IndexAtTokenCount(remaining, maxTokens);
            var space = remaining.LastIndexOfAny([' ', '\n'], Math.Max(cut - 1, 0));
            if (space > cut / 2)
            {
                cut = space;
            }

            windows.Add(remaining[..cut].Trim());

            // Step back by the overlap so a sentence cut in two appears whole in one of the chunks.
            var overlapStart = cut;
            if (overlapTokens > 0)
            {
                var head = remaining[..cut];
                var keep = tokens.IndexAtTokenCount(head, Math.Max(tokens.Count(head) - overlapTokens, 1));
                var boundary = head.IndexOfAny([' ', '\n'], keep);
                overlapStart = boundary > 0 && boundary < cut ? boundary : cut;
            }

            remaining = remaining[overlapStart..].Trim();
        }

        return windows;
    }
}
