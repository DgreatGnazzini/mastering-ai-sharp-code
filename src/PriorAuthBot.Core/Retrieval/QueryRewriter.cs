using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Prompts;

namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// Query rewriting for multi-query retrieval (section 8.5): the model writes a few alternative
/// phrasings of the question, each is searched, and the result lists are fused by rank. It helps
/// when a question is phrased nothing like the document, and costs one model call before retrieval
/// can start. The rewrites are search input only; they never reach the answer prompt.
/// </summary>
public sealed class QueryRewriter(IChatClient chatClient)
{
    public const int MaxQueryLength = 300;

    /// <summary>The original question first, then up to <paramref name="count"/> distinct rewrites.</summary>
    public async Task<IReadOnlyList<string>> ExpandAsync(string question, int count, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, PromptLibrary.Render(PromptLibrary.RewriteQuery, new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) })),
            new(ChatRole.User, $"<question>{question}</question>"),
        ];

        Rewrites result;
        try
        {
            result = await chatClient.GetValidatedResponseAsync<Rewrites>(messages, new ChatOptions { Temperature = 0 }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (StructuredOutputException)
        {
            // Rewriting is an optimization. If it fails, search the question as asked.
            return [question];
        }

        var queries = new List<string> { question };
        foreach (var query in result.Queries.Select(q => q.Trim()))
        {
            if (query.Length > 0 && query.Length <= MaxQueryLength && !queries.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(query);
            }

            if (queries.Count > count)
            {
                break;
            }
        }

        return queries;
    }

    public sealed record Rewrites([property: Required] List<string> Queries);
}
