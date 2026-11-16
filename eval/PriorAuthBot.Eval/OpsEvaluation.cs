using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Caching;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Eval;

internal sealed record CacheCase(string Id, string User, bool Same, string Question, string Variant);

internal sealed record CacheCaseSet(string Description, List<CacheCase> Cases)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CacheCaseSet Load() =>
        JsonSerializer.Deserialize<CacheCaseSet>(File.ReadAllText(EvalPaths.CacheCases), Json)
        ?? throw new InvalidOperationException("cache-cases.json is empty.");
}

/// <summary>One answer's footprint: what it cost and whether its audit record is complete.</summary>
internal sealed record OpsAnswer(string CaseId, long InputTokens, long OutputTokens, int ModelCalls, TimeSpan Elapsed, bool Declined, bool AuditComplete);

/// <summary>A cache pair's similarity, and what the cache did across permission sets.</summary>
internal sealed record CachePair(CacheCase Case, double Similarity, bool RepeatHit, bool CrossPartitionHit);

internal sealed record ThresholdRow(double Threshold, int ParaphraseHits, int Paraphrases, int FalseHits, int NearMisses);

internal sealed record OpsSummary(
    int Answers,
    int AuditRecords,
    int IncompleteAuditRecords,
    bool ChainValid,
    int TelemetryLeaks,
    int CrossPartitionHits,
    double AverageInputTokens,
    double AverageOutputTokens,
    long P95InputTokens,
    decimal CostPerAnswer,
    decimal MonthlyCost,
    IReadOnlyList<ThresholdRow> Thresholds,
    double ShippedThreshold);

/// <summary>
/// Chapter 13's eval section (--ops). It runs the judged answer sample as the API would, inside an
/// audited, metered request, and checks four things no baseline can excuse: every answer has a
/// complete audit record, the chain verifies, no span carries a question, and no cached answer
/// crosses a permission set. It also measures tokens and cost per answer, and how the answer cache's
/// similarity threshold trades paraphrase hits against wrong answers for this embedding model.
/// </summary>
internal static class OpsEvaluation
{
    /// <summary>Deductible Dynamics' planning number: 4,000 staff, 3 questions per working day, 21 working days.</summary>
    public const long QuestionsPerMonth = 4_000L * 3 * 21;

    public static readonly double[] Thresholds = [0.80, 0.85, 0.90, 0.93, 0.95, 0.97, 0.99];

    /// <summary>gpt-4.1-mini's list price, the hosted model PriorAuthBot would use, as a shadow price for every model.</summary>
    public static readonly TokenPrice Price = new() { InputPerMillion = 0.40m, OutputPerMillion = 1.60m };

    public static async Task<(OpsSummary Summary, IReadOnlyList<OpsAnswer> Answers, IReadOnlyList<CachePair> Pairs)> RunAsync(
        EvalSettings settings, GoldenSet set, IDistributedCache? cache, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(log);

        // 1. The judged sample, through the shipped pipeline, with a listener standing in for the exporter.
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("PriorAuthBot", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var answers = new List<OpsAnswer>();
        var questions = new List<string>();
        AuditVerification verification;
        int records;
        await using (var host = EvalHost.Create(settings.Configuration(set), cache))
        {
            await host.IngestSamplesAsync();
            var rag = host.Get<RagAnswerService>();
            var audit = host.Get<AuditLog>();
            foreach (var c in set.Cases.Where(c => c.AnswerEval))
            {
                var caller = set.Caller(c.User);
                questions.Add(c.Question);
                using var scope = AiRequestContext.Begin("POST /ask", caller.UserId, caller.DisplayName);
                var watch = Stopwatch.StartNew();
                var answer = await rag.AskAsync(c.Question, caller);
                watch.Stop();
                var record = await audit.WriteAsync(scope.Context, 200);

                // Complete: the question, the answer, and every document the answer showed the user.
                var complete = record.Prompt == c.Question
                    && record.Response == answer.Answer
                    && answer.Sources.All(s => record.Documents.Any(d => d.DocumentId == s.DocumentId))
                    && answer.Citations.All(s => record.Documents.Any(d => d.DocumentId == s.DocumentId && d.Access == AccessKind.Cited));
                answers.Add(new OpsAnswer(c.Id, record.InputTokens, record.OutputTokens, record.ModelCalls, watch.Elapsed, answer.Declined, complete));
                log($"  {c.Id}: {record.InputTokens} in, {record.OutputTokens} out, {record.Documents.Count} documents audited{(complete ? string.Empty : ", INCOMPLETE")}");
            }

            verification = await audit.VerifyAsync();
            records = (int)verification.Count;
        }

        var leaks = spans.Sum(s => questions.Count(q => SpanText(s).Contains(q, StringComparison.OrdinalIgnoreCase)));

        // 2. The answer cache: similarity of every pair under this embedding model, and the cache itself
        // across permission sets. The chat model plays no part here, so the offline client answers.
        var pairs = new List<CachePair>();
        var cacheConfig = settings.Configuration(set);
        cacheConfig["AnswerCache:Enabled"] = "true";
        double shipped;
        await using (var host = EvalHost.Create(cacheConfig, cache, chatClient: new OfflineChatClient()))
        {
            await host.IngestSamplesAsync();
            var rag = host.Get<RagAnswerService>();
            var answerCache = host.Get<SemanticAnswerCache>();
            shipped = new AnswerCacheOptions().SimilarityThreshold;
            foreach (var c in CacheCaseSet.Load().Cases)
            {
                // Each pair starts from an empty cache, so a hit for the other caller can only have come
                // from the owner's partition. (An earlier pair may have cached it in the other's own.)
                answerCache.Invalidate();
                var similarity = await answerCache.SimilarityAsync(c.Question, c.Variant);
                var owner = set.Caller(c.User);
                var other = set.Caller(c.User == "contractor" ? "staff" : "contractor");
                var first = await AskAsync(rag, c.Question, owner);
                var repeat = await AskAsync(rag, c.Question, owner);
                var crossed = await AskAsync(rag, c.Question, other);
                pairs.Add(new CachePair(c, similarity, !first.Declined && repeat.Hit, crossed.Hit));
                log($"  {c.Id}: similarity {similarity:0.000}{(c.Same ? " (paraphrase)" : " (different question)")}{(crossed.Hit ? ", CROSSED A PERMISSION SET" : string.Empty)}");
            }
        }

        var rows = Thresholds.Select(t => new ThresholdRow(
            t,
            pairs.Count(p => p.Case.Same && p.Similarity >= t),
            pairs.Count(p => p.Case.Same),
            pairs.Count(p => !p.Case.Same && p.Similarity >= t),
            pairs.Count(p => !p.Case.Same))).ToList();

        var answered = answers.Where(a => a.ModelCalls > 0).ToList();
        var averageIn = answered.Count == 0 ? 0 : answered.Average(a => (double)a.InputTokens);
        var averageOut = answered.Count == 0 ? 0 : answered.Average(a => (double)a.OutputTokens);
        var p95 = answered.Count == 0 ? 0 : answered.Select(a => a.InputTokens).Order().ElementAt((int)Math.Ceiling(0.95 * answered.Count) - 1);
        var perAnswer = Price.Cost((long)Math.Round(averageIn), (long)Math.Round(averageOut));
        var summary = new OpsSummary(
            answers.Count,
            records,
            answers.Count(a => !a.AuditComplete),
            verification.Valid,
            leaks,
            pairs.Count(p => p.CrossPartitionHit),
            averageIn,
            averageOut,
            p95,
            perAnswer,
            perAnswer * QuestionsPerMonth,
            rows,
            shipped);
        return (summary, answers, pairs);
    }

    private static async Task<(bool Declined, bool Hit)> AskAsync(RagAnswerService rag, string question, Core.Retrieval.CallerIdentity caller)
    {
        using var scope = AiRequestContext.Begin("POST /ask", caller.UserId, caller.DisplayName);
        var answer = await rag.AskAsync(question, caller);
        return (answer.Declined, scope.Context.AnswerCacheHit);
    }

    private static string SpanText(Activity span) =>
        string.Join("\n", span.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(span.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")))
            .Append(span.DisplayName));

    public static string Report(string provider, OpsSummary summary, IReadOnlyList<OpsAnswer> answers, IReadOnlyList<CachePair> pairs)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Observability, audit, and cost, {provider}: {summary.Answers} answers, {pairs.Count} cache pairs\n\n");
        s.Append("| Check | Result |\n|---|---|\n");
        s.Append(CultureInfo.InvariantCulture, $"| Audit records written | {summary.AuditRecords} of {summary.Answers} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Audit records missing a question, answer, or document | {summary.IncompleteAuditRecords} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Hash chain | {(summary.ChainValid ? "valid" : "BROKEN")} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Spans carrying a question | {summary.TelemetryLeaks} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Cached answers served to another permission set | {summary.CrossPartitionHits} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Exact repeats served from the cache | {pairs.Count(p => p.RepeatHit)} of {pairs.Count} |\n\n");

        s.Append("| Cost | Value |\n|---|---|\n");
        s.Append(CultureInfo.InvariantCulture, $"| Answers that called the model | {answers.Count(a => a.ModelCalls > 0)} of {answers.Count} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Input tokens per answer (mean, p95) | {summary.AverageInputTokens:0} , {summary.P95InputTokens} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Output tokens per answer (mean) | {summary.AverageOutputTokens:0} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Cost per answer at ${Price.InputPerMillion:0.00}/${Price.OutputPerMillion:0.00} per million | ${summary.CostPerAnswer:0.000000} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| A month at {QuestionsPerMonth:N0} questions, no cache | ${summary.MonthlyCost:0.00} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Time per answer (mean) | {(answers.Count == 0 ? 0 : answers.Average(a => a.Elapsed.TotalSeconds)):0.0} s |\n\n");

        s.Append(CultureInfo.InvariantCulture, $"Answer cache threshold (shipped: {summary.ShippedThreshold:0.00}). A false hit serves one question's answer for a different question.\n\n");
        s.Append("| Threshold | Paraphrases served | Different questions served (false hits) |\n|---|---|---|\n");
        foreach (var row in summary.Thresholds)
        {
            s.Append(CultureInfo.InvariantCulture, $"| {row.Threshold:0.00} | {row.ParaphraseHits} of {row.Paraphrases} | {row.FalseHits} of {row.NearMisses} |\n");
        }

        s.Append("\n| Pair | Kind | Similarity | Question | Variant |\n|---|---|---|---|---|\n");
        foreach (var p in pairs.OrderByDescending(p => p.Similarity))
        {
            s.Append(CultureInfo.InvariantCulture, $"| {p.Case.Id} | {(p.Case.Same ? "paraphrase" : "different")} | {p.Similarity:0.000} | {p.Case.Question} | {p.Case.Variant} |\n");
        }

        return s.Append('\n').ToString();
    }
}
