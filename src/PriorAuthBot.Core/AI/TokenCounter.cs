using Microsoft.ML.Tokenizers;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// Counts tokens with the o200k_base encoding (chapter 1). Local models tokenize differently, so
/// treat counts as close estimates and leave headroom in every budget.
/// </summary>
public sealed class TokenCounter
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public int Count(string text) => _tokenizer.CountTokens(text);

    /// <summary>Returns the index in <paramref name="text"/> after which the next token would exceed <paramref name="maxTokens"/>.</summary>
    public int IndexAtTokenCount(string text, int maxTokens) => _tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out _);
}
