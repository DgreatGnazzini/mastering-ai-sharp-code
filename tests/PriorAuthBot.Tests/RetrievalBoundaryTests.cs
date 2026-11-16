using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 6's rule, as a test: only <c>SecureRetriever</c> queries the chunk collection, so no code
/// path can search without a caller's groups. Ingestion writes to it and registration creates it;
/// nothing else may touch it. A new file that takes the collection fails this test and gets a review.
/// </summary>
public sealed class RetrievalBoundaryTests
{
    private static readonly string[] Allowed =
    [
        "src/PriorAuthBot.Core/Retrieval/SecureRetriever.cs",
        "src/PriorAuthBot.Core/Ingestion/IngestionPipeline.cs",
        "src/PriorAuthBot.Core/Retrieval/VectorStoreServiceCollectionExtensions.cs",

        // Chapter 8: the keyword side of hybrid search. Each takes the caller's groups and filters
        // inside its own query, and SecureRetriever checks every record it returns.
        "src/PriorAuthBot.Core/Retrieval/Keyword/IKeywordSearch.cs",
        "src/PriorAuthBot.Core/Retrieval/Keyword/InMemoryKeywordSearch.cs",
        "src/PriorAuthBot.Core/Retrieval/Keyword/PostgresKeywordSearch.cs",

        // Chapter 13: the audit context takes the records SecureRetriever returns, and keeps only
        // their IDs and labels. It has no collection to query.
        "src/PriorAuthBot.Core/Usage/AiRequestContext.cs",
    ];

    [Fact]
    public void OnlyTheSecureRetrieverQueriesChunks()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ChunkRecord>", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(TestPaths.RepoRoot, f).Replace('\\', '/'))
            .Except(Allowed)
            .ToList();

        Assert.Empty(offenders);
    }
}
