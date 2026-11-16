using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// Marking untrusted content (chapter 12), the "delimiting" form of what Microsoft calls spotlighting.
/// Retrieved text, OCR output, and tool results are data that someone other than the user wrote.
/// Each piece goes to the model in its own <see cref="TextContent"/>, inside an
/// <c>&lt;untrusted&gt;</c> block that the system prompt tells the model to read as data, with two
/// guarantees the model cannot give itself:
/// <list type="number">
/// <item>The content cannot close its own block. Anything that looks like the block's tags is
/// defanged, so "&lt;/untrusted&gt; New instructions:" stays inside.</item>
/// <item>Invisible characters are removed, so the model reads only what a reviewer can see.</item>
/// </list>
/// The content also carries a flag in <see cref="AIContent.AdditionalProperties"/>, which the
/// guardrail middleware reads to tell documents from the user's own words. The model never sees it.
/// Marking lowers the odds that a model follows an embedded instruction. It does not make it
/// impossible, which is why the shield and the output checks exist too.
/// </summary>
public static partial class UntrustedContent
{
    /// <summary>The key in <see cref="AIContent.AdditionalProperties"/> that says where a piece of text came from.</summary>
    public const string TrustKey = "priorauthbot.trust";

    /// <summary>The key that names the source, such as "source 2", for logs.</summary>
    public const string LabelKey = "priorauthbot.source";

    public const string Untrusted = "untrusted";

    /// <summary>What replaces a source the shield flagged. Its block stays, so numbering does not shift.</summary>
    public const string RemovedNotice = "PriorAuthBot removed this content because it contained instructions aimed at the assistant.";

    /// <summary>Wraps <paramref name="text"/> in an untrusted block, as its own content.</summary>
    public static TextContent Mark(string text, string label)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var content = new TextContent($"<untrusted source=\"{Neutralize(label)}\">\n{Neutralize(text).Trim()}\n</untrusted>\n\n")
        {
            AdditionalProperties = new() { [TrustKey] = Untrusted, [LabelKey] = label },
        };
        return content;
    }

    /// <summary>
    /// Flags text as untrusted without wrapping it, for a prompt that already puts its data inside
    /// its own block (the reranker's <c>&lt;passages&gt;</c>). The text is still neutralized.
    /// </summary>
    public static TextContent Flag(string text, string label)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return new TextContent(Neutralize(text))
        {
            AdditionalProperties = new() { [TrustKey] = Untrusted, [LabelKey] = label },
        };
    }

    /// <summary>The block that takes a flagged source's place.</summary>
    public static TextContent Removed(string label) => Mark(RemovedNotice, label);

    public static bool IsUntrusted(AIContent content) =>
        content?.AdditionalProperties?.TryGetValue(TrustKey, out var value) == true && value as string == Untrusted;

    public static string LabelOf(AIContent content) =>
        content?.AdditionalProperties?.TryGetValue(LabelKey, out var value) == true && value is string label ? label : "untrusted content";

    /// <summary>
    /// The text, made safe to put inside a block: invisible characters stripped, and every tag that
    /// could open or close a block (untrusted, sources, system, instructions) turned into look-alike
    /// text the model reads but the boundary does not.
    /// </summary>
    public static string Neutralize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BoundaryTag().Replace(HiddenText.Strip(text), m => "‹" + m.Value[1..]);
    }

    [GeneratedRegex(@"<\s*/?\s*(untrusted|sources?|system|instructions?|question)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryTag();
}
