using System.Text.Json;

namespace PriorAuthBot.Core.Documents;

/// <summary>
/// Title, policy number, department, and access list for a non-Markdown file (chapter 4). In a real
/// deployment these come from the source system, such as SharePoint. Here they come from a sidecar
/// file next to the document: <c>policy.pdf</c> has <c>policy.pdf.meta.json</c>. A file without one
/// gets no allowed groups, so ingestion never indexes it: deny by default, as in chapter 7.
/// </summary>
public sealed record DocumentMetadata(
    string? Title,
    string? Policy,
    string? Department,
    IReadOnlyList<string>? AllowedGroups,
    string? Sensitivity,
    bool Phi)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DocumentAccess Access => new(
        (AllowedGroups ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        Sensitivity ?? "Internal",
        Phi);

    public static async Task<DocumentMetadata> LoadForAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        var sidecar = documentPath + ".meta.json";
        if (!File.Exists(sidecar))
        {
            return new DocumentMetadata(null, null, null, [], null, false);
        }

        await using var stream = File.OpenRead(sidecar);
        return await JsonSerializer.DeserializeAsync<DocumentMetadata>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{sidecar} is empty.");
    }
}
