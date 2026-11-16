using System.Diagnostics;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Eval;

/// <summary>One retrieval configuration to compare, as configuration overrides.</summary>
internal sealed record RetrievalConfiguration(string Name, IReadOnlyDictionary<string, string?> Settings)
{
    public const string Chapter7 = "Vector only (chapter 7)";
    public const string Chapter8 = "Hybrid (chapter 8 default)";
    public const string Chapter8Reranked = "Hybrid + cross-encoder (chapter 8)";

    /// <summary>
    /// Chapter 7 against chapter 8: the same code with <c>Retrieval:Mode</c> switched, so the only
    /// difference is the thing being measured. The cross-encoder joins when its model is on disk.
    /// </summary>
    public static List<RetrievalConfiguration> Comparison(string? rerankerModel)
    {
        var list = new List<RetrievalConfiguration>
        {
            new(Chapter7, new Dictionary<string, string?> { ["Retrieval:Mode"] = "Vector" }),
            new(Chapter8, new Dictionary<string, string?> { ["Retrieval:Mode"] = "Hybrid" }),
        };
        if (rerankerModel is not null)
        {
            list.Add(new(Chapter8Reranked, new Dictionary<string, string?>
            {
                ["Retrieval:Mode"] = "Hybrid",
                ["Retrieval:Rerank:Provider"] = "CrossEncoder",
                ["Retrieval:Rerank:ModelPath"] = rerankerModel,
            }));
        }

        return list;
    }
}

internal sealed record RetrievalCaseResult(GoldenCase Case, IReadOnlyList<string> Ranked, IReadOnlyList<string> Leaks, int Top)
{
    public bool Returned => Ranked.Count > 0;

    public double RecallAt1 => RetrievalMetrics.RecallAtK(Ranked, Case.Expected, 1);

    public double RecallAtTop => RetrievalMetrics.RecallAtK(Ranked, Case.Expected, Top);

    public double ReciprocalRank => RetrievalMetrics.ReciprocalRank(Ranked, Case.Expected);
}

/// <summary>A configuration's results, summed over a subset of cases.</summary>
internal sealed record RetrievalSummary(string Name, int Cases, double RecallAt1, double RecallAtTop, double Mrr, int DeclinesKept, int Declines, int Leaks)
{
    public static RetrievalSummary Of(string name, IReadOnlyCollection<RetrievalCaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var withExpected = results.Where(r => r.Case.Expected.Length > 0).ToList();
        var declines = results.Where(r => r.Case.ExpectDecline).ToList();
        return new RetrievalSummary(
            name,
            withExpected.Count,
            withExpected.Count == 0 ? 1 : withExpected.Average(r => r.RecallAt1),
            withExpected.Count == 0 ? 1 : withExpected.Average(r => r.RecallAtTop),
            withExpected.Count == 0 ? 1 : withExpected.Average(r => r.ReciprocalRank),
            declines.Count(r => !r.Returned),
            declines.Count,
            results.Sum(r => r.Leaks.Count));
    }
}

internal sealed record RetrievalRun(RetrievalConfiguration Configuration, IReadOnlyList<RetrievalCaseResult> Results, TimeSpan Elapsed)
{
    public RetrievalSummary Summary(Func<GoldenCase, bool>? filter = null) =>
        RetrievalSummary.Of(Configuration.Name, Results.Where(r => filter?.Invoke(r.Case) ?? true).ToList());

    public int Leaks => Results.Sum(r => r.Leaks.Count);
}

internal static class RetrievalEvaluation
{
    /// <summary>Runs every golden question through <see cref="SecureRetriever"/>, as the person who asks it.</summary>
    public static async Task<RetrievalRun> RunAsync(
        RetrievalConfiguration configuration,
        EvalHost host,
        GoldenSet set,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(set);
        var retriever = host.Get<SecureRetriever>();
        var top = host.Get<Microsoft.Extensions.Options.IOptions<RetrievalOptions>>().Value.Top;
        var results = new List<RetrievalCaseResult>();
        var watch = Stopwatch.StartNew();
        foreach (var goldenCase in set.Cases)
        {
            var caller = set.Caller(goldenCase.User);
            var retrieved = await retriever.SearchAsync(goldenCase.Question, caller, cancellationToken).ConfigureAwait(false);
            results.Add(new RetrievalCaseResult(
                goldenCase,
                RetrievalMetrics.Documents(retrieved.Select(r => r.Record.DocumentId)),
                LeakageChecker.CheckRetrieved(goldenCase, caller, retrieved).ToList(),
                top));
        }

        return new RetrievalRun(configuration, results, watch.Elapsed);
    }
}
