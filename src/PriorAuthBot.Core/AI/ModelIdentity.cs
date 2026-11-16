using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// Chapter 15: the one string that names the model behind a client, <c>provider/model</c>, such as
/// <c>ollama/llama3.2:3b</c> or <c>onnxruntime-genai/phi-4-mini-instruct-onnx-cpu-int4</c>. Caches key on
/// it, the audit log records it, and the index stamps each chunk with the embedding model's.
/// </summary>
public static class ModelIdentity
{
    public static string Of(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var metadata = client.GetService<ChatClientMetadata>();
        return Format(metadata?.ProviderName, metadata?.DefaultModelId);
    }

    public static string Of(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        var id = Format(metadata?.ProviderName, metadata?.DefaultModelId);
        return metadata?.DefaultModelDimensions is { } dimensions ? $"{id}@{dimensions}" : id;
    }

    public static string Format(string? provider, string? model) =>
        $"{(string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.ToLowerInvariant())}/{(string.IsNullOrWhiteSpace(model) ? "unknown" : model)}";
}
