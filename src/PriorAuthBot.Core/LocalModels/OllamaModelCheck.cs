using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PriorAuthBot.Core.LocalModels;

/// <summary>
/// Chapter 15: checks the models an Ollama server has against models.lock.json. A tag such as
/// <c>llama3.2:3b</c> is a name, not a version: the publisher can push new weights under it, and the
/// next <c>ollama pull</c> takes them. The manifest digest Ollama reports is the version, so that is
/// what the lock pins.
/// </summary>
public static class OllamaModelCheck
{
    /// <summary>The name Ollama knows a locked model by: the part of the source after <c>ollama:</c>.</summary>
    public static string? OllamaName(LockedModel model) =>
        model?.Source is { } source && source.StartsWith("ollama:", StringComparison.Ordinal) ? source["ollama:".Length..] : null;

    public static async Task<IReadOnlyList<ModelVerification>> VerifyAsync(HttpClient http, IEnumerable<LockedModel> models, bool requireCommercialUse = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(models);
        var tags = await http.GetFromJsonAsync<TagsResponse>("api/tags", cancellationToken).ConfigureAwait(false);
        var installed = (tags?.Models ?? []).ToDictionary(m => m.Name, m => m.Digest, StringComparer.Ordinal);
        return [.. models.Where(m => m.Kind == "Ollama").Select(m => Check(m, installed, requireCommercialUse))];
    }

    public static ModelVerification Check(LockedModel model, IReadOnlyDictionary<string, string> installed, bool requireCommercialUse = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(installed);
        var problems = new List<string>();
        var name = OllamaName(model) ?? model.Id;
        if (requireCommercialUse && !model.CommercialUse)
        {
            problems.Add($"{model.Id} is licensed {model.License}, which models.lock.json records as not allowing commercial use.");
        }

        if (!installed.TryGetValue(name, out var digest))
        {
            problems.Add($"{name} is not pulled on this Ollama server.");
        }
        else if (!model.IsPinned)
        {
            problems.Add($"{name} has no recorded digest in models.lock.json (the server has {digest}).");
        }
        else if (!string.Equals(digest, model.Revision, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{name} on this server is {digest}; models.lock.json pins {model.Revision}. Someone pulled new weights under the same tag.");
        }

        return new ModelVerification(model.Id, problems);
    }

    private sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("digest")] string Digest);
}
