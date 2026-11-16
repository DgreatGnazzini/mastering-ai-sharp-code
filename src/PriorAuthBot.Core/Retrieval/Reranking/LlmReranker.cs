using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Prompts;

namespace PriorAuthBot.Core.Retrieval.Reranking;

/// <summary>
/// An LLM as the reranker (section 8.4): one call per question, with every candidate in the prompt,
/// asking for a 0 to 3 relevance score for each. Listwise rather than one call per passage, because
/// a dozen calls per question is a dozen times the latency and the bill. The chat client is
/// PriorAuthBot's own, so the PHI guard and the provider agreements from chapters 2 and 5 apply
/// to the passages it sends.
/// </summary>
public sealed class LlmReranker(IChatClient chatClient) : IReranker
{
    /// <summary>Passages are cut to this many characters, about 150 tokens, to keep the prompt small.</summary>
    private const int MaxPassageCharacters = 600;

    public string Name => "LLM (listwise, 0-3)";

    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(passages);
        if (passages.Count == 0)
        {
            return [];
        }

        var block = new StringBuilder();
        for (var i = 0; i < passages.Count; i++)
        {
            var text = passages[i].ReplaceLineEndings(" ");
            if (text.Length > MaxPassageCharacters)
            {
                text = text[..MaxPassageCharacters] + "…";
            }

            block.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {text}\n\n");
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, PromptLibrary.Get(PromptLibrary.RerankPassages)),
            new(ChatRole.User, $"<question>{query}</question>\n\n<passages>\n{block.ToString().TrimEnd()}\n</passages>"),
        ];
        var result = await chatClient.GetValidatedResponseAsync<RelevanceScores>(messages, new ChatOptions { Temperature = 0 }, cancellationToken: cancellationToken).ConfigureAwait(false);

        // A passage the model skipped scores 0, so it cannot climb by being left out.
        var scores = new double[passages.Count];
        foreach (var score in result.Scores)
        {
            if (score.Passage >= 1 && score.Passage <= passages.Count)
            {
                scores[score.Passage - 1] = score.Score;
            }
        }

        return scores;
    }

    public sealed record RelevanceScores([property: Required] List<PassageScore> Scores);

    public sealed record PassageScore(int Passage, [property: Range(0, 3)] int Score);
}
