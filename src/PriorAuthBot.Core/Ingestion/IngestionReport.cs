namespace PriorAuthBot.Core.Ingestion;

/// <summary>What one ingestion run changed. Logged, and returned by the admin endpoint.</summary>
public sealed record IngestionReport(int Added, int Updated, int Unchanged, int Removed, int SkippedNoAcl, int ChunksWritten);
