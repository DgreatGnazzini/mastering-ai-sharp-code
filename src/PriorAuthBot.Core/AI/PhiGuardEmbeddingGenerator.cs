using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Privacy;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// The embedding side of chapter 4's PHI guard (chapter 5). An embedding request sends the full text
/// to the provider, just like a chat request, so a provider that is not approved for PHI gets
/// redacted text here too.
/// </summary>
public sealed class PhiGuardEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> innerGenerator)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    public override Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GenerateAsync(values.Select(v => PhiRedactor.Redact(v).Text).ToList(), options, cancellationToken);
}
