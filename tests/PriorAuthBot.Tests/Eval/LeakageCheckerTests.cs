using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Eval;
using Xunit;

namespace PriorAuthBot.Tests.Eval;

/// <summary>
/// Tests for the test: a leak check that never fires looks exactly like a system that never leaks.
/// Each check is shown a leak on purpose and must catch it.
/// </summary>
public sealed class LeakageCheckerTests
{
    private const string SepsisRule = "A positive screen must be escalated to the supervising physician within 15 minutes.";

    private static readonly SourceDocument Sepsis = Document("sepsis-screening-protocol", ["Clinical"], $"# Sepsis\n\n## Escalation\n\n{SepsisRule}");
    private static readonly SourceDocument Pto = Document("pto-policy", ["AllStaff"], "# PTO\n\nYou may carry over up to 40 hours of unused PTO into the next calendar year.");
    private static readonly GoldenCase ContractorAsksSepsis = new("t1", "contractor", "When must a positive sepsis screen be escalated?", null, ["sepsis-screening-protocol"], true, null, AnswerMustNotContain: ["15 minutes"]);
    private static readonly CallerIdentity Contractor = new("c", "contractor", ["Contractors"]);
    private static readonly CallerIdentity Nurse = new("n", "nurse", ["AllStaff", "Clinical"]);

    private readonly LeakageChecker _checker = new([Sepsis, Pto]);

    [Fact]
    public void CatchesAChunkTheCallerHasNoGroupFor()
    {
        var chunk = new RetrievedChunk(new ChunkRecord { DocumentId = "sepsis-screening-protocol", ChunkId = "sepsis#0", AllowedGroups = ["Clinical"] }, 0.9);
        Assert.Single(LeakageChecker.CheckRetrieved(ContractorAsksSepsis, Contractor, [chunk]));
    }

    [Fact]
    public void CatchesRestrictedTextInThePrompt()
    {
        var prompt = $"<sources>\n[1] {SepsisRule}\n</sources>\n\nQuestion: When must a positive sepsis screen be escalated?";
        Assert.Single(_checker.CheckPrompt(ContractorAsksSepsis, Contractor, prompt));

        // The same prompt is fine for a nurse, who may read the protocol.
        Assert.Empty(_checker.CheckPrompt(ContractorAsksSepsis, Nurse, prompt));
    }

    [Fact]
    public void CatchesARestrictedFactInTheAnswer()
    {
        Assert.Single(LeakageChecker.CheckAnswer(ContractorAsksSepsis, "Escalate it within 15 minutes."));
        Assert.Empty(LeakageChecker.CheckAnswer(ContractorAsksSepsis, "I don't know based on the documents you can access."));
    }

    [Fact]
    public void HeadingsAreTooShortToCount() =>
        Assert.DoesNotContain("## Escalation", LeakageChecker.Sentences(Sepsis.Text));

    private static SourceDocument Document(string id, string[] groups, string text) =>
        new(id, id, text, new DocumentAccess(groups, "Internal", false), "Test", null, id + ".md");
}
