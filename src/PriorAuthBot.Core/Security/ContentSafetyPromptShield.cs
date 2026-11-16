using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// Azure AI Content Safety's Prompt Shields (chapter 12): a classifier trained on jailbreaks and on
/// instructions hidden in documents, called over REST. It signs in with Entra ID like everything else
/// in PriorAuthBot, so there is no key to leak. The text it checks leaves the trust boundary, so the
/// resource belongs in the same agreements (and region) as the chat model; the PHI guard runs first,
/// so a provider that is not approved for PHI never sees it unredacted.
/// </summary>
public sealed class ContentSafetyPromptShield(HttpClient http, TokenCredential credential, PromptShieldOptions options) : IPromptShield
{
    public const string Name = "content-safety";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    public async Task<ShieldVerdict> AnalyzeAsync(string? userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var endpoint = options.Endpoint ?? throw new InvalidOperationException("Security:PromptShield:Endpoint is not set.");
        var uri = new Uri(endpoint, string.Create(CultureInfo.InvariantCulture, $"contentsafety/text:shieldPrompt?api-version={options.ApiVersion}"));
        var limit = Math.Max(1, options.MaxCharactersPerCall);

        // A long document is checked in pieces; it is an attack if any piece is.
        var pieces = documents
            .SelectMany((d, i) => Split(HiddenText.Reveal(d), limit).Select(p => (Index: i, Text: p)))
            .ToList();
        var attacks = new bool[documents.Count];
        var userAttack = false;
        var prompt = userPrompt is null ? null : HiddenText.Reveal(userPrompt);
        if (prompt is not null && prompt.Length > limit)
        {
            prompt = prompt[..limit];
        }

        var next = 0;
        do
        {
            var batch = new List<(int Index, string Text)>();
            var used = prompt?.Length ?? 0;
            while (next < pieces.Count && batch.Count < options.MaxDocumentsPerCall && used + pieces[next].Text.Length <= limit)
            {
                used += pieces[next].Text.Length;
                batch.Add(pieces[next++]);
            }

            if (batch.Count == 0 && next < pieces.Count)
            {
                // The prompt fills this call; send the documents in the next one.
                batch.Add(pieces[next++]);
                prompt = null;
            }

            var result = await CallAsync(uri, prompt, [.. batch.Select(b => b.Text)], cancellationToken).ConfigureAwait(false);
            userAttack |= result.UserPromptAnalysis?.AttackDetected == true;
            for (var i = 0; i < batch.Count; i++)
            {
                attacks[batch[i].Index] |= result.DocumentsAnalysis?.ElementAtOrDefault(i)?.AttackDetected == true;
            }

            prompt = null;
        }
        while (next < pieces.Count);

        return new ShieldVerdict(userAttack, attacks, Name);
    }

    private async Task<ShieldResponse> CallAsync(Uri uri, string? userPrompt, string[] documents, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new ShieldRequest(userPrompt ?? string.Empty, documents)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShieldResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Prompt Shields returned an empty response.");
    }

    private static IEnumerable<string> Split(string text, int size)
    {
        if (text.Length == 0)
        {
            yield return text;
        }

        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    internal sealed record ShieldRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] string[] Documents);

    internal sealed record Analysis([property: JsonPropertyName("attackDetected")] bool AttackDetected);

    internal sealed record ShieldResponse(
        [property: JsonPropertyName("userPromptAnalysis")] Analysis? UserPromptAnalysis,
        [property: JsonPropertyName("documentsAnalysis")] List<Analysis>? DocumentsAnalysis);
}
