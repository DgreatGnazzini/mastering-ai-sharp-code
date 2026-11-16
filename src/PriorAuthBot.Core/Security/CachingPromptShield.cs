using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// Remembers verdicts by a hash of the text (chapter 12). The same chunk is checked when the RAG
/// service screens it and again when the middleware sees it, and a tool loop sends the whole history
/// on every round trip; without this, each of those is another call to the shield service. Only
/// hashes and booleans are kept, never the text. The cache is per process and bounded.
/// It also decides what happens when the shield fails: block, or fall back to the heuristics.
/// </summary>
public sealed partial class CachingPromptShield(IPromptShield inner, PromptShieldOptions options, ILogger<CachingPromptShield> logger) : IPromptShield
{
    private const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, bool> _verdicts = new(StringComparer.Ordinal);

    public int Calls { get; private set; }

    public async Task<ShieldVerdict> AnalyzeAsync(string? userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        bool? user = userPrompt is null ? false : Known("u", userPrompt);
        var known = documents.Select(d => Known("d", d)).ToList();
        if (user is not null && known.All(k => k is not null))
        {
            return new ShieldVerdict(user.Value, [.. known.Select(k => k!.Value)], "cache");
        }

        var missing = Enumerable.Range(0, documents.Count).Where(i => known[i] is null).ToList();
        ShieldVerdict verdict;
        try
        {
            Calls++;
            verdict = await inner.AnalyzeAsync(user is null ? userPrompt : null, [.. missing.Select(i => documents[i])], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            if (!string.Equals(options.OnError, PromptShieldProviders.Heuristic, StringComparison.OrdinalIgnoreCase))
            {
                LogUnavailable(logger, ex, "blocking the request");
                throw new PromptShieldUnavailableException("The prompt shield is unavailable, so the request was not sent.", ex);
            }

            LogUnavailable(logger, ex, "falling back to the heuristic checks");
            verdict = await new HeuristicPromptShield().AnalyzeAsync(user is null ? userPrompt : null, [.. missing.Select(i => documents[i])], cancellationToken).ConfigureAwait(false);
        }

        if (_verdicts.Count > MaxEntries)
        {
            _verdicts.Clear();
        }

        if (user is null)
        {
            user = verdict.UserPromptAttack;
            _verdicts[Key("u", userPrompt!)] = user.Value;
        }

        for (var j = 0; j < missing.Count; j++)
        {
            known[missing[j]] = verdict.DocumentAttacks[j];
            _verdicts[Key("d", documents[missing[j]])] = verdict.DocumentAttacks[j];
        }

        return new ShieldVerdict(user.Value, [.. known.Select(k => k!.Value)], verdict.Detector);
    }

    private bool? Known(string kind, string text) => _verdicts.TryGetValue(Key(kind, text), out var v) ? v : null;

    private static string Key(string kind, string text) =>
        kind + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "The prompt shield failed; {Action}.")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string action);
}
