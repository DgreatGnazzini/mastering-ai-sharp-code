using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Ingestion;

/// <summary>
/// Load, chunk, embed, upsert (chapter 7). Each run syncs the index with the full set of source
/// documents: new and changed documents are (re)written, unchanged ones are skipped, and documents
/// that disappeared from the source, or lost their ACL, are deleted from the index.
/// </summary>
public sealed partial class IngestionPipeline(
    IChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStoreCollection<Guid, ChunkRecord> collection,
    ILogger<IngestionPipeline> logger,
    Caching.SemanticAnswerCache? answerCache = null,
    Microsoft.Extensions.Options.IOptions<VectorStoreOptions>? storeOptions = null)
{
    /// <summary>
    /// Chapter 15: the embedding model every chunk this pipeline writes is stamped with. Vectors from
    /// two models are not comparable, so one collection holds one model's vectors, and this is how
    /// ingestion and retrieval tell.
    /// </summary>
    private readonly string _embeddingModel = AI.ModelIdentity.Of(embeddingGenerator);

    private const int EmbeddingBatchSize = 32;
    private const int MaxDocuments = 100_000;

    /// <summary>
    /// Syncs the index with <paramref name="documents"/>, which must be the complete current set:
    /// anything indexed but missing from it is deleted. Pass a partial set and you delete the rest.
    /// </summary>
    public async Task<IngestionReport> SyncAsync(IAsyncEnumerable<SourceDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        // Chapter 14: in Azure the migration job creates the table and the API's role may not create
        // anything, so only ask for the table when it is missing (a local database, a test).
        if (!await collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
        }

        // Chunk 0 of every document carries the document's hash and chunk count.
        var indexed = new Dictionary<string, ChunkRecord>(StringComparer.Ordinal);
        await foreach (var first in collection.GetAsync(r => r.ChunkIndex == 0, MaxDocuments, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            indexed[first.DocumentId] = first;
        }

        // Chapter 15: a collection built by another embedding model. Writing this model's vectors next to
        // those would leave a collection where half the scores mean nothing, so by default ingestion
        // refuses, and the fix is a new collection (VectorStore:Collection) built beside the old one.
        // For a local index you can rebuild at will, OnEmbeddingModelChange=Reembed re-embeds every document.
        var stale = indexed.Values.Where(r => !string.Equals(r.EmbeddingModel, _embeddingModel, StringComparison.Ordinal)).ToList();
        if (stale.Count > 0)
        {
            var found = stale.Select(r => r.EmbeddingModel is { Length: > 0 } m ? m : "(none recorded)").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            if (storeOptions?.Value.OnEmbeddingModelChange != EmbeddingModelChange.Reembed)
            {
                throw new EmbeddingModelMismatchException(
                    $"The collection holds {stale.Count} document(s) embedded with {string.Join(", ", found)}, and the configured embedding model is {_embeddingModel}. " +
                    "Point VectorStore:Collection at a new collection and let ingestion build it, or set VectorStore:OnEmbeddingModelChange to Reembed for an index you can rebuild in place.");
            }

            LogReembedding(logger, stale.Count, string.Join(", ", found), _embeddingModel);
        }

        int added = 0, updated = 0, unchanged = 0, removed = 0, skipped = 0, chunksWritten = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var document in documents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            seen.Add(document.Id);
            indexed.TryGetValue(document.Id, out var existing);

            if (document.Access.IsDenyAll)
            {
                // Deny by default: a document with no ACL is never indexed, and loses any old chunks.
                LogNoAcl(logger, document.Id);
                skipped++;
                if (existing is not null)
                {
                    await DeleteChunksAsync(existing, cancellationToken).ConfigureAwait(false);
                    removed++;
                }

                continue;
            }

            var hash = Hash(document);
            if (existing?.DocumentHash == hash && string.Equals(existing.EmbeddingModel, _embeddingModel, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            // Delete before writing. If the ACL shrank, old chunks must not stay searchable under
            // the old groups, even for the seconds it takes to re-embed the document.
            if (existing is not null)
            {
                await DeleteChunksAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            chunksWritten += await WriteChunksAsync(document, hash, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        foreach (var (documentId, first) in indexed)
        {
            if (!seen.Contains(documentId))
            {
                await DeleteChunksAsync(first, cancellationToken).ConfigureAwait(false);
                LogRemoved(logger, documentId);
                removed++;
            }
        }

        var report = new IngestionReport(added, updated, unchanged, removed, skipped, chunksWritten);

        // Chapter 13: a changed document or ACL can change any answer, and can take a document away
        // from a group. Every cached answer is dropped, rather than guessing which ones it affects.
        if (added + updated + removed > 0)
        {
            answerCache?.Invalidate();
        }

        LogReport(logger, report.Added, report.Updated, report.Unchanged, report.Removed, report.SkippedNoAcl, report.ChunksWritten);
        return report;
    }

    private async Task<int> WriteChunksAsync(SourceDocument document, string hash, CancellationToken cancellationToken)
    {
        var chunks = chunker.Chunk(document);
        var records = new List<ChunkRecord>(chunks.Count);

        foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
        {
            // Embed the chunk with its title and section, so "Carryover" is found by a question
            // about carrying over PTO even though the section text never says "PTO".
            var inputs = batch.Select(c => EmbeddingInput(document, c)).ToList();
            var embeddings = await embeddingGenerator.GenerateAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < batch.Length; i++)
            {
                var chunk = batch[i];
                records.Add(new ChunkRecord
                {
                    Key = ChunkRecord.MakeKey(document.Id, chunk.Index),
                    ChunkId = ChunkRecord.MakeChunkId(document.Id, chunk.Index),
                    DocumentId = document.Id,
                    ChunkIndex = chunk.Index,
                    ChunkCount = chunks.Count,
                    DocumentHash = hash,
                    Title = document.Title,
                    PolicyNumber = document.PolicyNumber,
                    Section = chunk.Section,
                    Text = chunk.Text,
                    SourcePath = Path.GetFileName(document.SourcePath),
                    Page = PageOf(document, chunk),
                    SearchText = (document.PolicyNumber is null ? string.Empty : document.PolicyNumber + " ") + EmbeddingInput(document, chunk),
                    AllowedGroups = [.. document.Access.AllowedGroups],
                    Sensitivity = document.Access.Sensitivity,
                    Department = document.Department,
                    ContainsPhi = document.Access.ContainsPhi,
                    Embedding = embeddings[i].Vector,
                    EmbeddingModel = _embeddingModel,
                });
            }
        }

        await collection.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
        return records.Count;
    }

    private Task DeleteChunksAsync(ChunkRecord first, CancellationToken cancellationToken) =>
        collection.DeleteAsync(Enumerable.Range(0, Math.Max(first.ChunkCount, 1)).Select(i => ChunkRecord.MakeKey(first.DocumentId, i)), cancellationToken);

    internal static string EmbeddingInput(SourceDocument document, TextChunk chunk) =>
        chunk.Section.Length == 0
            ? $"{document.Title}\n\n{chunk.Text}"
            : $"{document.Title} > {chunk.Section}\n\n{chunk.Text}";

    /// <summary>
    /// The page a chunk starts on: the first page whose text contains the chunk's opening words.
    /// Whitespace is collapsed on both sides, because chunking normalizes it. Null when the document
    /// has no pages, or the opening words cannot be found (citations then name the section only).
    /// </summary>
    internal static int? PageOf(SourceDocument document, TextChunk chunk)
    {
        if (document.Pages is not { Count: > 0 } pages)
        {
            return null;
        }

        var opening = Collapse(chunk.Text);
        opening = opening[..Math.Min(opening.Length, 60)];
        if (opening.Length == 0)
        {
            return null;
        }

        return pages.FirstOrDefault(p => Collapse(p.Text).Contains(opening, StringComparison.Ordinal))?.Number;

        static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Hash(SourceDocument document)
    {
        var material = string.Join('\n', document.Title, document.PolicyNumber, document.Department,
            string.Join(',', document.Access.AllowedGroups), document.Access.Sensitivity, document.Access.ContainsPhi, document.Text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    [LoggerMessage(EventId = 1502, Level = LogLevel.Warning, Message = "Re-embedding {Count} document(s) in place: they were embedded with {Found}, and the configured model is {Model}.")]
    private static partial void LogReembedding(ILogger logger, int count, string found, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} has no allowed groups and was not indexed.")]
    private static partial void LogNoAcl(ILogger logger, string documentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} is gone from the source and was removed from the index.")]
    private static partial void LogRemoved(ILogger logger, string documentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion: {Added} added, {Updated} updated, {Unchanged} unchanged, {Removed} removed, {Skipped} skipped with no ACL, {Chunks} chunks written.")]
    private static partial void LogReport(ILogger logger, int added, int updated, int unchanged, int removed, int skipped, int chunks);
}
