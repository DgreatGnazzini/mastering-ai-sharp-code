using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Rag;

/// <summary>The numbered sources block sent to the model, and the chunks it contains.</summary>
public sealed record AssembledContext(string Text, IReadOnlyList<RetrievedChunk> Sources, int TokenCount);

/// <summary>
/// Turns retrieved chunks into the sources block (chapter 7): best first, duplicates removed,
/// at most a few chunks per document, and never more than the token budget.
/// </summary>
public sealed class ContextAssembler(TokenCounter tokens, IOptions<ContextOptions> options)
{
    public AssembledContext Assemble(IReadOnlyList<RetrievedChunk> retrieved)
    {
        ArgumentNullException.ThrowIfNull(retrieved);
        var settings = options.Value;
        var used = new List<RetrievedChunk>();
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        var perDocument = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        var total = 0;

        foreach (var chunk in retrieved.OrderByDescending(c => c.Score))
        {
            var record = chunk.Record;
            if (!seenText.Add(Normalize(record.Text)))
            {
                continue;
            }

            if (perDocument.GetValueOrDefault(record.DocumentId) >= settings.MaxChunksPerDocument)
            {
                continue;
            }

            var block = Format(used.Count + 1, record);
            var blockTokens = tokens.Count(block);
            if (total + blockTokens > settings.MaxContextTokens)
            {
                // Skip rather than stop: a smaller, lower-ranked chunk may still fit.
                continue;
            }

            builder.Append(block);
            total += blockTokens;
            used.Add(chunk);
            perDocument[record.DocumentId] = perDocument.GetValueOrDefault(record.DocumentId) + 1;
        }

        return new AssembledContext(builder.ToString().TrimEnd(), used, total);
    }

    private static string Format(int number, ChunkRecord record)
    {
        var heading = record.PolicyNumber is null ? record.Title : $"{record.Title} ({record.PolicyNumber})";
        if (record.Section.Length > 0)
        {
            heading += " - " + record.Section;
        }

        return string.Create(CultureInfo.InvariantCulture, $"[{number}] {heading}\n{record.Text}\n\n");
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
