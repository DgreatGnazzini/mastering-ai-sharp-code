using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Usage;
using Xunit;

namespace PriorAuthBot.Tests.Observability;

/// <summary>Chapter 13: the hash chain finds a changed, missing, or reordered record.</summary>
public sealed class AuditChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUntouchedLogVerifies()
    {
        var store = await StoreWithAsync(5);

        var result = AuditChain.Verify(await AllAsync(store));

        Assert.True(result.Valid, result.Problem);
        Assert.Equal(5, result.Count);
        Assert.Equal(1, result.FirstSequence);
        Assert.Equal(5, result.LastSequence);
    }

    [Fact]
    public async Task EachRecordPointsAtTheOneBefore()
    {
        var records = await AllAsync(await StoreWithAsync(3));

        Assert.Equal(AuditChain.Genesis, records[0].PreviousHash);
        Assert.Equal(records[0].Hash, records[1].PreviousHash);
        Assert.Equal(records[1].Hash, records[2].PreviousHash);
    }

    [Fact]
    public async Task AChangedAnswerIsFound()
    {
        var store = await StoreWithAsync(5);
        store.Tamper(3, r => r with { Response = "Nothing to see here." });

        var result = AuditChain.Verify(await AllAsync(store));

        Assert.False(result.Valid);
        Assert.Contains("Record 3 was changed", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecomputedHashBreaksTheNextLink()
    {
        // An attacker who rewrites a record and fixes its own hash still breaks record 4's pointer.
        var store = await StoreWithAsync(5);
        store.Tamper(3, r =>
        {
            var changed = r with { Documents = [] };
            return changed with { Hash = AuditChain.ComputeHash(changed) };
        });

        var result = AuditChain.Verify(await AllAsync(store));

        Assert.False(result.Valid);
        Assert.Contains("Record 4 does not point at record 3", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovedRecordIsFound()
    {
        var store = await StoreWithAsync(5);
        store.Remove(2);

        var result = AuditChain.Verify(await AllAsync(store));

        Assert.False(result.Valid);
        Assert.Contains("Record 2 is missing", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordsPurgedAtTheEndOfRetentionLeaveAValidChain()
    {
        // Retention removes the oldest records; the chain is checked from the first one left.
        var store = await StoreWithAsync(5);
        store.Remove(1);
        store.Remove(2);

        var result = AuditChain.Verify(await AllAsync(store));

        Assert.True(result.Valid, result.Problem);
        Assert.Equal(3, result.FirstSequence);
    }

    [Fact]
    public async Task TheHashCoversEveryField()
    {
        var record = (await AllAsync(await StoreWithAsync(1)))[0];

        Assert.NotEqual(record.Hash, AuditChain.ComputeHash(record with { UserId = "someone-else" }));
        Assert.NotEqual(record.Hash, AuditChain.ComputeHash(record with { At = record.At.AddSeconds(1) }));
        Assert.NotEqual(record.Hash, AuditChain.ComputeHash(record with { Groups = ["AllStaff", "Compliance"] }));
        Assert.Equal(record.Hash, AuditChain.ComputeHash(record));
    }

    [Fact]
    public void ARecordRoundTripsThroughJsonWithTheSameHash()
    {
        // The Postgres store keeps the JSON and reads it back; the hash must survive that.
        var record = AuditChain.Seal(Sample(1), 1, AuditChain.Genesis);
        var json = System.Text.Json.JsonSerializer.Serialize(record, AuditChain.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AuditRecord>(json, AuditChain.Json)!;

        Assert.Equal(record.Hash, AuditChain.ComputeHash(back));
    }

    internal static AuditRecord Sample(int i) => new()
    {
        Id = $"audit-{i:D4}",
        At = new DateTimeOffset(2026, 11, 15, 9, 0, i % 60, TimeSpan.Zero),
        UserId = "demo-nurse",
        DisplayName = "Nina Okafor, RN",
        Groups = ["AllStaff", "Clinical"],
        Operation = "POST /ask",
        Status = 200,
        Prompt = "When must a positive sepsis screen be escalated?",
        Response = "Within 15 minutes [1].",
        Documents = [new TouchedDocument("sepsis-screening-protocol", "Sepsis Screening Protocol", "Internal", false, AccessKind.Cited, ["sepsis-screening-protocol:0001"])],
        ModelCalls = 1,
        InputTokens = 812,
        OutputTokens = 31,
        Cost = 0.000374m,
    };

    private static async Task<InMemoryAuditStore> StoreWithAsync(int count)
    {
        var store = new InMemoryAuditStore();
        for (var i = 1; i <= count; i++)
        {
            await store.AppendAsync(Sample(i), Ct);
        }

        return store;
    }

    private static async Task<List<AuditRecord>> AllAsync(InMemoryAuditStore store)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in store.QueryAsync(new AuditQuery(), Ct))
        {
            records.Add(record);
        }

        return records;
    }
}
