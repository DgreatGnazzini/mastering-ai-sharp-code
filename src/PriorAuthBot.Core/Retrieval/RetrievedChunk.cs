namespace PriorAuthBot.Core.Retrieval;

/// <summary>A chunk returned by <see cref="SecureRetriever"/>, with its similarity score.</summary>
public sealed record RetrievedChunk(ChunkRecord Record, double Score);
