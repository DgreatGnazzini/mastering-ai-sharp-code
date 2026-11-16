using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Eval;

internal sealed record AnswerCaseResult(
    GoldenCase Case,
    string Answer,
    bool Declined,
    IReadOnlyList<string> Leaks,
    IReadOnlyDictionary<string, double> Scores)
{
    /// <summary>Null when the case lists no fact to look for.</summary>
    public bool? ContainsExpected => Case.MustContain.Length == 0 || Declined
        ? (Declined && Case.MustContain.Length > 0 ? false : null)
        : Case.MustContain.All(p => Answer.Contains(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Averages over the answers that were scored. A metric no judge could score is left out, and counted.</summary>
internal sealed record AnswerSummary(
    int Asked,
    int Scored,
    IReadOnlyDictionary<string, double> Averages,
    int ContainsExpected,
    int WithExpectedFact,
    int WrongDeclines,
    int MissedDeclines,
    int Leaks)
{
    public static AnswerSummary Of(IReadOnlyCollection<AnswerCaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var averages = AnswerEvaluation.MetricNames
            .Select(m => (m, values: results.Where(r => r.Scores.ContainsKey(m)).Select(r => r.Scores[m]).ToList()))
            .Where(x => x.values.Count > 0)
            .ToDictionary(x => x.m, x => x.values.Average());
        return new AnswerSummary(
            results.Count,
            results.Count(r => r.Scores.Count > 0),
            averages,
            results.Count(r => r.ContainsExpected == true),
            results.Count(r => r.ContainsExpected is not null),
            results.Count(r => r.Declined && !r.Case.ExpectDecline && r.Case.Expected.Length > 0),
            results.Count(r => !r.Declined && r.Case.ExpectDecline),
            results.Sum(r => r.Leaks.Count));
    }
}

/// <summary>
/// Answer quality (chapter 9). Each question is asked through <see cref="RagAnswerService"/> as its
/// user, then checked twice: by rules (the expected fact is in the answer, no restricted fact is,
/// and the prompt held nothing the user may not read) and, for the judged sample, by LLM judges
/// from Microsoft.Extensions.AI.Evaluation: relevance, groundedness, completeness against the
/// expected answer, and chapter 8's citation faithfulness. Judge calls go through the reporting
/// library's response cache, and every result goes to its result store for the HTML report.
/// </summary>
internal static class AnswerEvaluation
{
    public static readonly string[] MetricNames =
    [
        RelevanceEvaluator.RelevanceMetricName,
        GroundednessEvaluator.GroundednessMetricName,
        CompletenessEvaluator.CompletenessMetricName,
        CitationFaithfulnessEvaluator.FaithfulnessMetricName,
        CitationFaithfulnessEvaluator.CoverageMetricName,
    ];

    public static IEvaluator[] Evaluators() =>
    [
        new RelevanceEvaluator(),
        new GroundednessEvaluator(),
        new CompletenessEvaluator(),
        new CitationFaithfulnessEvaluator(),
    ];

    /// <param name="cases">The questions to ask.</param>
    /// <param name="judged">The ones to score with the judges; null scores none (an offline run).</param>
    public static async Task<List<AnswerCaseResult>> RunAsync(
        EvalHost host,
        GoldenSet set,
        IEnumerable<GoldenCase> cases,
        LeakageChecker leaks,
        ReportingConfiguration? judged,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(leaks);
        var rag = host.Get<RagAnswerService>();
        var retriever = host.Get<SecureRetriever>();
        var assembler = host.Get<ContextAssembler>();
        var results = new List<AnswerCaseResult>();

        foreach (var goldenCase in cases)
        {
            var caller = set.Caller(goldenCase.User);
            host.Prompts.Clear();
            var answer = await rag.AskAsync(goldenCase.Question, caller, cancellationToken).ConfigureAwait(false);

            var found = new List<string>();
            foreach (var prompt in host.Prompts.Prompts)
            {
                found.AddRange(leaks.CheckPrompt(goldenCase, caller, prompt));
            }

            found.AddRange(LeakageChecker.CheckAnswer(goldenCase, answer.Answer));

            var scores = new Dictionary<string, double>();
            if (judged is not null && goldenCase.AnswerEval && !answer.Declined)
            {
                // The same retrieval the service just ran (embeddings are cached), to give the judges the exact sources.
                var context = assembler.Assemble(await retriever.SearchAsync(goldenCase.Question, caller, cancellationToken).ConfigureAwait(false));
                await using var scenario = await judged.CreateScenarioRunAsync(goldenCase.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
                var result = await scenario.EvaluateAsync(
                    RagAnswerService.BuildMessages(goldenCase.Question, context),
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, answer.Answer)),
                    [
                        new GroundednessEvaluatorContext(context.Text),
                        new CompletenessEvaluatorContext(goldenCase.ExpectedAnswer ?? string.Join("; ", goldenCase.MustContain)),
                        new CitationSourcesContext(context.Sources.Select(s => s.Record.Text).ToList()),
                    ],
                    cancellationToken).ConfigureAwait(false);

                foreach (var name in MetricNames)
                {
                    if (result.Metrics.TryGetValue(name, out var metric) && metric is NumericMetric { Value: { } value })
                    {
                        scores[name] = value;
                    }
                }
            }

            log?.Invoke($"{goldenCase.Id} ({goldenCase.User}): {(answer.Declined ? "declined" : answer.Answer)}");
            results.Add(new AnswerCaseResult(goldenCase, answer.Answer, answer.Declined, found, scores));
        }

        return results;
    }
}
