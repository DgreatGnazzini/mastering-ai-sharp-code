namespace PriorAuthBot.Core.Documents;

/// <summary>
/// Loads Deductible Dynamics' Markdown policies, each with a small front-matter block such as:
/// <code>
/// ---
/// title: Paid Time Off Policy
/// policy: HR-104
/// department: HR
/// allowed-groups: [AllStaff]
/// sensitivity: Internal
/// phi: false
/// ---
/// </code>
/// In a real deployment the ACL comes from the source system (SharePoint, a file share, a document
/// management system). Front matter stands in for it so the samples stay self-contained.
/// </summary>
public static class MarkdownDocumentLoader
{
    public static async IAsyncEnumerable<SourceDocument> LoadDirectoryAsync(
        string directory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            yield return Parse(Path.GetFileNameWithoutExtension(path), raw, path);
        }
    }

    public static SourceDocument Parse(string id, string raw, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = raw.ReplaceLineEndings("\n");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                text = text[(end + 5)..];
            }
        }

        var access = new DocumentAccess(
            AllowedGroups: ParseList(fields.GetValueOrDefault("allowed-groups")),
            Sensitivity: fields.GetValueOrDefault("sensitivity") ?? "Internal",
            ContainsPhi: bool.TryParse(fields.GetValueOrDefault("phi"), out var phi) && phi);

        return new SourceDocument(
            Id: id,
            Title: fields.GetValueOrDefault("title") ?? id,
            Text: text.Trim(),
            Access: access,
            Department: fields.GetValueOrDefault("department") ?? "AllStaff",
            PolicyNumber: fields.GetValueOrDefault("policy"),
            SourcePath: sourcePath);
    }

    private static string[] ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.Trim('"', '\''))
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
