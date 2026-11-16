using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Tests.Observability;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>
/// Chapter 14: the audit log's second copy and anchors. Each pass copies what is new, and refuses to
/// copy anything that does not continue the chain from the last anchor.
/// </summary>
public sealed class AuditArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachPassCopiesOnlyTheNewRecordsAndAnchorsTheLast()
    {
        var (store, archive, archiver) = Create();
        await AppendAsync(store, 3);

        var first = await archiver.RunOnceAsync(Ct);
        await AppendAsync(store, 2);
        var second = await archiver.RunOnceAsync(Ct);
        var third = await archiver.RunOnceAsync(Ct);

        Assert.Equal(3, first.Archived);
        Assert.Equal(2, second.Archived);
        Assert.Equal(0, third.Archived);
        Assert.Equal(5, second.Anchor!.Sequence);
        Assert.Equal(4, second.Anchor.FirstSequence);
        Assert.Contains("records/000000000001-000000000003.jsonl", archive.Blobs.Keys);
        Assert.Contains("anchors/000000000005.json", archive.Blobs.Keys);
        Assert.Equal(2, archive.Blobs["records/000000000004-000000000005.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task ARemovedRecordAfterTheAnchorStopsTheArchive()
    {
        var (store, _, archiver) = Create();
        await AppendAsync(store, 3);
        await archiver.RunOnceAsync(Ct);
        await AppendAsync(store, 2);

        store.Remove(4);
        var result = await archiver.RunOnceAsync(Ct);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Archived);
        Assert.Contains("does not continue the chain", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARewrittenChainDoesNotMatchTheAnchor()
    {
        var (store, _, archiver) = Create();
        await AppendAsync(store, 3);
        await archiver.RunOnceAsync(Ct);
        await AppendAsync(store, 1);

        // Someone with full database rights changes record 4 and recomputes its hash. It still looks
        // sealed, but it no longer points at the anchored hash of record 3.
        store.Tamper(4, r => AuditChain.Seal(r with { Response = "nothing to see" }, r.Sequence, "f00d"));
        var result = await archiver.RunOnceAsync(Ct);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task AnArchiveNameIsWrittenOnce()
    {
        var archive = new InMemoryAuditArchive();
        var anchor = new AuditAnchor(1, "h", 1, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await archive.WriteAsync([], anchor, Ct);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => archive.WriteAsync([], anchor, Ct));
    }

    private static (InMemoryAuditStore Store, InMemoryAuditArchive Archive, AuditArchive Archiver) Create()
    {
        var store = new InMemoryAuditStore();
        var archive = new InMemoryAuditArchive();
        var archiver = new AuditArchive(store, archive, Options.Create(new AuditOptions()), NullLogger<AuditArchive>.Instance);
        return (store, archive, archiver);
    }

    private static async Task AppendAsync(InMemoryAuditStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await store.AppendAsync(AuditChainTests.Sample(store.Count + 1) with { Id = Guid.NewGuid().ToString("N") }, Ct);
        }
    }
}
