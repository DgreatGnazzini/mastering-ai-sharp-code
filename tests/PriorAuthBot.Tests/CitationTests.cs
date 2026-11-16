using System.Net;
using System.Net.Http.Json;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Structured citations (chapter 8): [n] markers become document, section, page, and link; a
/// marker that points at no source is removed; and a link opens only for callers who could have
/// retrieved the document, with the same 404 for "restricted" and "does not exist".
/// </summary>
public sealed class CitationTests(ApiTests.PriorAuthBotFactory factory) : IClassFixture<ApiTests.PriorAuthBotFactory>
{
    private static readonly CallerIdentity Nurse = new("nurse", "Nurse", ["AllStaff", "Clinical"]);

    [Fact]
    public void ResolveKeepsValidMarkersAndRemovesTheRest()
    {
        var (answer, cited, removed) = Citations.Resolve("Escalate within 15 minutes [1]. Document it [2, 7]. Call the lab [9].", sourceCount: 2);

        Assert.Equal("Escalate within 15 minutes [1]. Document it [2]. Call the lab.", answer);
        Assert.Equal([1, 2], cited);
        Assert.Equal(2, removed);
    }

    [Fact]
    public void SentencesCarryTheirCitations()
    {
        var sentences = Citations.Sentences("Standard requests take 5 business days [1]. Urgent ones take 72 hours [1][2]. That is all.");

        Assert.Equal(3, sentences.Count);
        Assert.Equal("Standard requests take 5 business days.", sentences[0].Text);
        Assert.Equal([1], sentences[0].Sources);
        Assert.Equal([1, 2], sentences[1].Sources);
        Assert.Empty(sentences[2].Sources);
    }

    [Fact]
    public void LinksPointAtThePageOrTheSection()
    {
        var section = new ChunkRecord { DocumentId = "pto-policy", Section = "PTO Policy > Carryover" };
        var paged = new ChunkRecord { DocumentId = "formulary-exception-process", Section = "Steps", Page = 2 };

        Assert.Equal("/documents/pto-policy#carryover", Citations.Link(section));
        Assert.Equal("/documents/formulary-exception-process#page-2", Citations.Link(paged));
        Assert.Equal("prior-authorization-for-mri-cpt-72148", Citations.Slug("Prior Authorization for MRI (CPT 72148)"));
    }

    [Fact]
    public async Task ChunksFromAPdfKnowTheirPage()
    {
        var document = await new DocumentLoader().LoadAsync(Path.Combine(TestPaths.SampleFiles, "formulary-exception-process.pdf"), TestContext.Current.CancellationToken);
        var chunker = new StructuralChunker(new Core.AI.TokenCounter(), new ChunkingOptions { MaxTokens = 120, OverlapTokens = 0 });

        var pages = chunker.Chunk(document).Select(c => IngestionPipeline.PageOf(document, c)).ToList();

        Assert.All(pages, p => Assert.NotNull(p));
        Assert.Equal(1, pages[0]);
        Assert.Equal(document.Pages!.Count, pages.Max());
    }

    [Fact]
    public async Task AnswerReturnsStructuredCitations()
    {
        var model = new RecordingChatClient(_ => "Escalate to the physician within 15 minutes [1]. Start the bundle [5].");
        await using var host = RagTestHost.Create(chatClient: model);
        await host.IngestSamplesAsync();

        var answer = await host.Get<RagAnswerService>().AskAsync("When must a positive sepsis screen be escalated to a physician?", Nurse, TestContext.Current.CancellationToken);

        var citation = Assert.Single(answer.Citations);
        Assert.Equal(1, citation.Number);
        Assert.Equal("sepsis-screening-protocol", citation.DocumentId);
        Assert.StartsWith("sepsis-screening-protocol:", citation.ChunkId, StringComparison.Ordinal);
        Assert.StartsWith("/documents/sepsis-screening-protocol#", citation.Link, StringComparison.Ordinal);
        Assert.DoesNotContain("[5]", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CitationLinkOpensForTheNurse()
    {
        var response = await Client("nurse").GetAsync("/documents/sepsis-screening-protocol", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<DocumentView>(TestContext.Current.CancellationToken);
        Assert.Equal("Sepsis Screening Protocol", document!.Title);
        Assert.Contains(document.Sections, s => s.Text.Contains("15 minutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestrictedAndMissingDocumentsLookTheSame()
    {
        var restricted = await Client("contractor").GetAsync("/documents/sepsis-screening-protocol", TestContext.Current.CancellationToken);
        var missing = await Client("contractor").GetAsync("/documents/no-such-document", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, restricted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(WithoutTraceId(await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)), WithoutTraceId(await restricted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task UnindexedDraftCannotBeOpenedByLink()
    {
        var response = await Client("hr").GetAsync("/documents/draft-layoff-plan", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string WithoutTraceId(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", string.Empty);

    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-User", user);
        return client;
    }
}
