using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace PriorAuthBot.Core.Mcp;

/// <summary>A tool that passed vetting, or the reason it did not.</summary>
public sealed record VettingResult(string ServerName, string ToolName, bool Accepted, bool ReadOnly, string Fingerprint, IReadOnlyList<string> Problems);

/// <summary>
/// Checks an MCP server's tools before the model sees them (chapter 10). A tool description is a
/// prompt written by someone else, and the model reads it with the same trust as yours, so it gets
/// the same review: an allowlist, a pinned fingerprint, and a scan for instructions hidden in it.
/// </summary>
public static partial class McpToolVetting
{
    public const int MaxDescriptionLength = 1_000;

    /// <summary>SHA-256 over the tool's name, description, and input schema, in lowercase hex.</summary>
    public static string Fingerprint(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var schema = JsonSerializer.Serialize(tool.InputSchema);
        var text = string.Join('\n', tool.Name, tool.Description ?? string.Empty, schema);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static VettingResult Vet(string serverName, McpServerEntry server, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        var problems = new List<string>();
        var fingerprint = Fingerprint(tool);

        if (!server.Tools.TryGetValue(tool.Name, out var entry))
        {
            problems.Add("not on the allowlist for this server");
        }
        else if (entry.Sha256 is null)
        {
            problems.Add($"no pinned fingerprint; vet it and pin {fingerprint}");
        }
        else if (!string.Equals(entry.Sha256, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"its description or schema changed since it was vetted (pinned {entry.Sha256[..12]}, now {fingerprint[..12]})");
        }

        problems.AddRange(Lint(tool.Description ?? string.Empty));
        problems.AddRange(Lint(JsonSerializer.Serialize(tool.InputSchema)).Select(p => "input schema: " + p));
        return new VettingResult(serverName, tool.Name, problems.Count == 0, entry?.ReadOnly == true, fingerprint, problems);
    }

    /// <summary>
    /// Signs of a poisoned description: text addressed to the model rather than describing the tool.
    /// This is a tripwire, not a proof of safety; the pinned fingerprint and a human review are the control.
    /// </summary>
    public static IEnumerable<string> Lint(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxDescriptionLength)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"longer than {MaxDescriptionLength} characters ({text.Length})");
        }

        if (Security.HiddenText.Contains(text))
        {
            yield return "contains invisible or direction-changing Unicode characters";
        }

        foreach (Match match in Suspicious().Matches(text))
        {
            yield return $"contains \"{match.Value}\"";
        }
    }

    // Zero-width characters, bidirectional overrides, and Unicode tag characters are found by
    // HiddenText (chapter 12), which the guardrails share with this check.
    [GeneratedRegex(
        @"<\s*/?\s*(important|system|instructions?)\s*>|ignore (all |any )?(previous|prior|above|other) (instructions|rules)|do not (tell|mention|inform|show)|(don't|never) (tell|mention|inform)|system prompt|before (using|calling) (this|any) tool|other tools?|you must (also )?(call|read|send|include)|(api[_ ]?key|password|token|secret|\.ssh|\.env)\b|https?://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Suspicious();
}
