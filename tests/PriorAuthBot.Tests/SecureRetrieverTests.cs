using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

public class SecureRetrieverTests
{
    [Fact]
    public async Task CallerWithNoGroupsGetsNothing()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();

        var results = await host.Get<SecureRetriever>().SearchAsync("How do I report a privacy incident?", new CallerIdentity("nobody", "Nobody", []), TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FilterRunsInsideTheQuery()
    {
        // Every chunk is retrievable (threshold off), and Clinical chunks score highest for this
        // question. If filtering happened after the query, the contractor would get fewer than Top
        // results. Because it happens inside the query, the store fills the page with visible chunks.
        await using var host = RagTestHost.Create(new Dictionary<string, string?> { ["Retrieval:MinScore"] = "-1", ["Retrieval:Top"] = "5" });
        await host.IngestSamplesAsync();
        var contractor = new CallerIdentity("contractor", "Contractor", ["Contractors"]);

        var results = await host.Get<SecureRetriever>().SearchAsync("sepsis screening escalation to the physician", contractor, TestContext.Current.CancellationToken);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(contractor.IsInAnyGroup(r.Record.AllowedGroups)));
    }

    [Fact]
    public async Task SameQuestionDifferentUsersDifferentSources()
    {
        await using var host = RagTestHost.Create();
        await host.IngestSamplesAsync();
        var retriever = host.Get<SecureRetriever>();
        var ct = TestContext.Current.CancellationToken;

        var nurse = await retriever.SearchAsync("Can I work remotely?", new CallerIdentity("n", "Nurse", ["AllStaff", "Clinical"]), ct);
        var contractor = await retriever.SearchAsync("Can I work remotely?", new CallerIdentity("c", "Contractor", ["Contractors"]), ct);

        Assert.Equal("remote-work-policy", nurse[0].Record.DocumentId);
        Assert.Equal("contractor-handbook", contractor[0].Record.DocumentId);
    }
}
