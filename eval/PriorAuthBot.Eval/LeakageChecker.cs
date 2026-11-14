using System.Text.RegularExpressions;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Eval;

/// <summary>
/// The "must not see" checks (chapter 9). A leak is reported at three places, and a run with any
/// leak fails whatever the baselines say:
/// <list type="number">
/// <item>Retrieval: a chunk the caller has no group for, or one of the case's mustNotSee documents.</item>
/// <item>The prompt: any sentence of a document the caller may not read, in the text sent to the model.</item>
/// <item>The answer: a fact listed in answerMustNotContain, which only a restricted document states.</item>
/// </list>
/// The first is what chapters 6 and 7 enforce. The second catches a bug after retrieval, such as a
/// cache or a "helpful" context step that adds text. The third catches what neither can see, such
/// as a model that memorized a restricted fact from an earlier prompt.
/// </summary>
internal sealed partial class LeakageChecker
{
    /// <summary>Sentences shorter than this ("## Scope") are too common to prove anything.</summary>
    private const int MinSentenceLength = 30;

    private readonly IReadOnlyList<(SourceDocument Document, IReadOnlyList<string> Sentences)> _documents;

    public LeakageChecker(IEnumerable<SourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents.Select(d => (d, (IReadOnlyList<string>)Sentences(d.Text))).ToList();
    }

    public static async Task<LeakageChecker> ForSamplesAsync(CancellationToken cancellationToken = default) =>
        new(await MarkdownDocumentLoader.LoadDirectoryAsync(EvalPaths.SampleDocuments, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false));

    public static IEnumerable<string> CheckRetrieved(GoldenCase goldenCase, CallerIdentity caller, IEnumerable<RetrievedChunk> retrieved)
    {
        ArgumentNullException.ThrowIfNull(goldenCase);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(retrieved);
        foreach (var chunk in retrieved)
        {
            if (!caller.IsInAnyGroup(chunk.Record.AllowedGroups))
            {
                yield return $"{goldenCase.Id}: retrieved {chunk.Record.ChunkId}, which {caller.DisplayName} has no group for.";
            }
            else if (goldenCase.Forbidden.Contains(chunk.Record.DocumentId))
            {
                yield return $"{goldenCase.Id}: retrieved {chunk.Record.DocumentId}, which is on the case's must-not-see list.";
            }
        }
    }

    /// <summary>Every sentence of every document the caller may not read must be absent from the prompt.</summary>
    public IEnumerable<string> CheckPrompt(GoldenCase goldenCase, CallerIdentity caller, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var visible = _documents.Where(d => caller.IsInAnyGroup(d.Document.Access.AllowedGroups)).SelectMany(d => d.Sentences).ToHashSet();
        foreach (var (document, sentences) in _documents.Where(d => !caller.IsInAnyGroup(d.Document.Access.AllowedGroups)))
        {
            var leaked = sentences.FirstOrDefault(s => !visible.Contains(s) && prompt.Contains(s, StringComparison.Ordinal));
            if (leaked is not null)
            {
                yield return $"{goldenCase.Id}: the prompt for {caller.DisplayName} contains text from {document.Id}: \"{leaked}\"";
            }
        }
    }

    public static IEnumerable<string> CheckAnswer(GoldenCase goldenCase, string answer)
    {
        ArgumentNullException.ThrowIfNull(goldenCase);
        ArgumentNullException.ThrowIfNull(answer);
        return goldenCase.MustNotContain
            .Where(fact => answer.Contains(fact, StringComparison.OrdinalIgnoreCase))
            .Select(fact => $"{goldenCase.Id}: the answer for {goldenCase.User} states \"{fact}\", which only a restricted document says.");
    }

    internal static List<string> Sentences(string text) =>
        SentenceSplit().Split(text)
            .Select(s => s.Trim().TrimStart('-', '#', ' ').Trim())
            .Where(s => s.Length >= MinSentenceLength)
            .Distinct()
            .ToList();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceSplit();
}
