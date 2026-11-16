using System.Text.RegularExpressions;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// A prompt shield made of regular expressions (chapter 12). It runs anywhere, costs nothing, and
/// catches the attacks everyone copies from the same lists: "ignore previous instructions", "you are
/// now DAN", text addressed to "the AI", and images that carry data off site. It misses anything
/// phrased a new way, so it is a tripwire, not a defense on its own. In production, put Azure AI
/// Content Safety's Prompt Shields in front of it (<see cref="ContentSafetyPromptShield"/>), and
/// keep the output checks behind both.
/// </summary>
public sealed partial class HeuristicPromptShield : IPromptShield
{
    public const string Name = "heuristic";

    public Task<ShieldVerdict> AnalyzeAsync(string? userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var user = userPrompt is not null && IsUserAttack(userPrompt);
        return Task.FromResult(new ShieldVerdict(user, [.. documents.Select(IsDocumentAttack)], Name));
    }

    /// <summary>A direct attack: the user asks the model to drop its rules, change roles, or show its prompt.</summary>
    public static bool IsUserAttack(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var visible = HiddenText.Reveal(text);
        return Override().IsMatch(visible) || RolePlay().IsMatch(visible) || PromptExtraction().IsMatch(visible);
    }

    /// <summary>
    /// An indirect attack: a document that talks to the model instead of to its human reader. Policy
    /// text says "employees must"; injected text says "AI assistant, you must". Hidden characters in
    /// a document are an attack on their own, whatever they spell.
    /// </summary>
    public static bool IsDocumentAttack(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (HiddenText.Contains(text))
        {
            return true;
        }

        return Override().IsMatch(text) || RolePlay().IsMatch(text) || PromptExtraction().IsMatch(text)
            || AddressedToModel().IsMatch(text) || Exfiltration().IsMatch(text);
    }

    [GeneratedRegex(
        @"\b(ignore|disregard|forget|override|bypass|skip)\b.{0,40}\b(previous|prior|above|earlier|all|any|your|the|these|those|other)\b.{0,30}\b(instructions?|rules|guidelines|directions|prompts?|guardrails|restrictions)\b"
        + @"|\bnew (instructions|rules)\s*:"
        + @"|\b(do not|don't|never) (ask|wait) (me |the user )?for (approval|confirmation|permission)\b"
        + @"|\bwithout (asking for |waiting for )?(approval|confirmation)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Override();

    [GeneratedRegex(
        @"\byou are (now|no longer)\b|\b(act|behave|respond) as (if you (were|are)|an? (unrestricted|unfiltered|jailbroken|different))\b|\b(DAN|developer mode|jailbreak|god mode)\b|\bpretend (that )?you (have|had|are) no\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RolePlay();

    [GeneratedRegex(
        @"\b(reveal|repeat|print|show|output|display|tell me|give me|what (is|are)|leak|dump)\b.{0,40}\b(your|the|its)\b.{0,20}\b(system prompt|system message|initial (prompt|instructions)|hidden (prompt|instructions|rules)|instructions you were given|rules you (follow|were given))\b"
        + @"|\b(text|everything|words) (above|before) (this|my) (message|line)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromptExtraction();

    [GeneratedRegex(
        @"\b(note|message|instructions?) (to|for) (the )?(ai|assistant|model|chatbot|llm|bot)\b"
        + @"|\b(ai|assistant|model|chatbot|llm)s?\b[,:]?\s*(you )?(must|should|shall|will)\b"
        + @"|\b(if|when) you are an? (ai|language model|assistant|llm)\b"
        + @"|\bwhen (you )?(summariz|answer|respond|repl)\w*.{0,40}\b(always|also|instead|must)\b"
        + @"|\b(tell|instruct|ask) (the )?(user|reader|employee|staff member)s? to (email|send|share|enter|provide|confirm) (their|your|a) (password|credentials|login|ssn|social security)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AddressedToModel();

    // A Markdown image or link whose URL carries a query string or a template to fill: the shape of
    // every "render this image and the data goes with it" exfiltration.
    [GeneratedRegex(
        @"!\[[^\]]*\]\(\s*https?://[^)\s]*[?#=][^)]*\)|\{\{?\s*(question|conversation|chat|history|user|answer|secret|data)[^}]*\}\}?|\b(append|add|include) (the |this )?(user'?s? )?(question|conversation|chat history|answer|data) (to|in) (the |this )?(url|link|image)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Exfiltration();
}
