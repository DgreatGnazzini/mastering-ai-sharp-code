using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.AI;

namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// One chunk of one document, as stored in the vector store (chapter 6). The access-control
/// fields are on the record from the first insert, and <see cref="AllowedGroups"/> is indexed so
/// the store can filter on it inside the query.
/// </summary>
public sealed class ChunkRecord
{
    /// <summary>
    /// Deterministic, from the document ID and chunk index, so re-ingestion overwrites instead of
    /// duplicating. A Guid rather than a readable string because it is the one key type every store
    /// in chapter 6 accepts: Qdrant takes only Guids and integers, Azure AI Search only strings and Guids.
    /// </summary>
    [VectorStoreKey]
    public Guid Key { get; set; }

    /// <summary>The readable form of the key, <c>{DocumentId}:{ChunkIndex:D4}</c>, for logs and citations.</summary>
    [VectorStoreData]
    public string ChunkId { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string DocumentId { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public int ChunkIndex { get; set; }

    /// <summary>How many chunks the document had when this chunk was written, used to clean up on re-ingestion.</summary>
    [VectorStoreData]
    public int ChunkCount { get; set; }

    /// <summary>Hash of the document's text and ACL. A change to either triggers re-ingestion.</summary>
    [VectorStoreData]
    public string DocumentHash { get; set; } = string.Empty;

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    /// <summary>Indexed from chapter 10, so the lookup_policy tool can filter on it in every store.</summary>
    [VectorStoreData(IsIndexed = true)]
    public string? PolicyNumber { get; set; }

    /// <summary>The heading path the chunk came from, such as "Accrual &gt; Carryover".</summary>
    [VectorStoreData]
    public string Section { get; set; } = string.Empty;

    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>The page the chunk starts on, for PDFs and scans (chapter 8). Null for formats without pages.</summary>
    [VectorStoreData]
    public int? Page { get; set; }

    /// <summary>
    /// What keyword search matches against (chapter 8): policy number, title, section, and text, the
    /// same material that is embedded. Marked full-text indexed, so pgvector creates a GIN index on
    /// <c>to_tsvector('english', "SearchText")</c> and Azure AI Search makes the field searchable.
    /// </summary>
    [VectorStoreData(IsFullTextIndexed = true)]
    public string SearchText { get; set; } = string.Empty;

    // Security metadata (chapter 6). Copied from the source document's ACL at ingestion.

    [VectorStoreData(IsIndexed = true)]
    public List<string> AllowedGroups { get; set; } = [];

    [VectorStoreData(IsIndexed = true)]
    public string Sensitivity { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string Department { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public bool ContainsPhi { get; set; }

    /// <summary>
    /// Chapter 15: the embedding model that made <see cref="Embedding"/>, as <see cref="AI.ModelIdentity"/>
    /// names it (<c>ollama/nomic-embed-text:v1.5@768</c>). Retrieval refuses a chunk from another model,
    /// because its score against this model's query vector means nothing.
    /// </summary>
    [VectorStoreData]
    public string EmbeddingModel { get; set; } = string.Empty;

    [VectorStoreVector(ChunkDimensions.Size, DistanceFunction = DistanceFunction.CosineSimilarity, IndexKind = IndexKind.Hnsw)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    public static string MakeChunkId(string documentId, int chunkIndex) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{documentId}:{chunkIndex:D4}");

    /// <summary>A name-based Guid: the first 16 bytes of the SHA-256 of the chunk ID, marked as version 8.</summary>
    public static Guid MakeKey(string documentId, int chunkIndex)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(MakeChunkId(documentId, chunkIndex)), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
