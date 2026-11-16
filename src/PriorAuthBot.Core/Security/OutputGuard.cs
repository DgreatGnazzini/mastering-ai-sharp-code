using System.Text.RegularExpressions;
using PriorAuthBot.Core.Privacy;

namespace PriorAuthBot.Core.Security;

/// <summary>What the output checks changed in one answer.</summary>
public sealed record OutputCheck(string Text, bool SystemPromptLeak, int IdentifiersRedacted, SanitizeResult Markdown)
{
    public bool Changed => SystemPromptLeak || IdentifiersRedacted > 0 || Markdown.Total > 0;
}

/// <summary>
/// The checks on a model's answer before anyone sees it (chapter 12). They do not depend on the model
/// behaving, which is the point: whatever an injection talked the model into writing, these run on it.
/// <list type="number">
/// <item>System prompt leak (OWASP LLM07): the answer repeats a long run of words from a system
/// prompt. The whole answer is replaced. (The system prompt should hold nothing secret anyway; this
/// stops the embarrassing version of the leak, not a breach.)</item>
/// <item>Ungrounded identifiers (LLM02): a member ID, MRN, email, or phone number that was not in
/// the request is redacted; an SSN always is.</item>
/// <item>Unsafe Markdown (LLM05): images, off-site links, and HTML, through <see cref="MarkdownSanitizer"/>.</item>
/// </list>
/// </summary>
public sealed partial class OutputGuard(SecurityOptions options)
{
    public const string SystemPromptRefusal = "I can't share how I'm configured. Ask me about Deductible Dynamics policies instead.";

    /// <param name="answer">The model's text.</param>
    /// <param name="systemPrompts">The system messages of the request.</param>
    /// <param name="ground">Everything else the model was given: the user's words, sources, and tool results.</param>
    /// <param name="groundIsComplete">False when the request also held content that is not text, such as
    /// an image: identifiers read from it cannot be checked against <paramref name="ground"/>, so only
    /// SSNs are redacted.</param>
    public OutputCheck Check(string answer, IEnumerable<string> systemPrompts, string ground, bool groundIsComplete = true)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(systemPrompts);
        ArgumentNullException.ThrowIfNull(ground);
        var empty = new SanitizeResult(string.Empty, 0, 0, 0);
        if (RepeatsSystemPrompt(answer, systemPrompts, ground, options.SystemPromptOverlapWords))
        {
            return new OutputCheck(SystemPromptRefusal, true, 0, empty);
        }

        var redacted = PhiRedactor.RedactUngrounded(answer, ground, groundIsComplete);
        var markdown = MarkdownSanitizer.Sanitize(redacted.Text, options.AllowedLinkHosts);
        return new OutputCheck(markdown.Text, false, redacted.Total, markdown);
    }

    /// <summary>
    /// True when <paramref name="answer"/> contains <paramref name="window"/> consecutive words of a
    /// system prompt that are not also in <paramref name="ground"/>. Words are compared lowercase with
    /// punctuation dropped, so reformatting the prompt as a list does not hide it. A run that the user
    /// or a source also contains is not a leak: the model may quote a source that happens to agree.
    /// </summary>
    public static bool RepeatsSystemPrompt(string answer, IEnumerable<string> systemPrompts, string ground, int window)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(systemPrompts);
        ArgumentNullException.ThrowIfNull(ground);
        var answerWords = Words(answer);
        if (answerWords.Count < window)
        {
            return false;
        }

        var answerRuns = Runs(answerWords, window);
        var groundRuns = Runs(Words(ground), window);
        return systemPrompts.Any(p => Runs(Words(p), window).Any(r => answerRuns.Contains(r) && !groundRuns.Contains(r)));
    }

    private static List<string> Words(string text) =>
        [.. Word().Matches(text.ToLowerInvariant()).Select(m => m.Value)];

    private static HashSet<string> Runs(List<string> words, int window)
    {
        var runs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + window <= words.Count; i++)
        {
            runs.Add(string.Join(' ', words.Skip(i).Take(window)));
        }

        return runs;
    }

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
