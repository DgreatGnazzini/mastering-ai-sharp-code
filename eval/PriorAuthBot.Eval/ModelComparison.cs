using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PriorAuthBot.Core.LocalModels;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Eval;

/// <summary>Chapter 15: how long answers take and what they cost, measured with no cache.</summary>
internal sealed record LatencySummary(
    int Answers,
    double P50Seconds,
    double P95Seconds,
    double AverageInputTokens,
    double AverageOutputTokens,
    double OutputTokensPerSecond,
    decimal CostPerAnswer);

/// <summary>
/// One eval run, as JSON (eval-summary-{provider}.json): the numbers a model comparison reads. Two of
/// these, one from the current model and one from the candidate, are what an upgrade is decided on.
/// </summary>
internal sealed record EvalSummary(
    string Provider,
    string Model,
    string? Judge,
    string EmbeddingModel,
    string Execution,
    double RecallAtTop,
    double Mrr,
    double DeclinesKept,
    int Leaks,
    IReadOnlyDictionary<string, double>? Answers,
    int? WrongDeclines,
    int? MissedDeclines,
    double? ToolSelection,
    double? ToolArguments,
    int? RedTeamAbsoluteBreaches,
    int? RedTeamMeasuredBreaches,
    LatencySummary? Latency,
    bool GateOk,
    IReadOnlyList<string> GateFailures)
{
    public static EvalSummary Load(string path) =>
        JsonSerializer.Deserialize<EvalSummary>(File.ReadAllText(path), Baselines.Json) ?? throw new InvalidOperationException($"{path} is empty.");

    public static EvalSummary Of(
        EvalSettings settings, RetrievalSummary retrieval, int retrievalLeaks, AnswerSummary? answers, ToolSummary? tools,
        RedTeamSummary? redTeam, LatencySummary? latency, GateResult? gate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(retrieval);
        Dictionary<string, double>? scores = null;
        if (answers is not null)
        {
            scores = answers.Averages.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3));
            if (answers.WithExpectedFact > 0)
            {
                scores[RegressionGate.ContainsExpected] = Math.Round((double)answers.ContainsExpected / answers.WithExpectedFact, 3);
            }
        }

        return new EvalSummary(
            settings.Provider,
            settings.AnsweringModel,
            settings.IsLive && answers is not null ? settings.JudgingModel : null,
            settings.Provider == EvalSettings.Offline ? "offline/hashing-v1" : settings.Provider == EvalSettings.AzureOpenAI ? EvalSettings.AzureEmbeddingDeployment : EvalSettings.EmbeddingModel,
            settings.ExecutionName,
            Math.Round(retrieval.RecallAtTop, 3),
            Math.Round(retrieval.Mrr, 3),
            Math.Round(retrieval.Declines == 0 ? 1 : (double)retrieval.DeclinesKept / retrieval.Declines, 3),
            retrievalLeaks + (answers?.Leaks ?? 0) + (tools?.Leaks ?? 0),
            scores,
            answers?.WrongDeclines,
            answers?.MissedDeclines,
            tools is null ? null : Math.Round(tools.Selection, 3),
            tools is null ? null : Math.Round(tools.Arguments, 3),
            redTeam?.AbsoluteBreaches,
            redTeam?.MeasuredBreaches,
            latency,
            gate?.Ok ?? true,
            gate?.Failed ?? []);
    }
}

internal static class LatencyEvaluation
{
    /// <summary>
    /// Asks the first <paramref name="samples"/> questions of the judged sample with the response
    /// cache off, so every answer is a real model call, and times each one. Tokens come from the
    /// model's own usage, as chapter 13's metering records them; cost is at the shadow price chapter 13
    /// uses for every model, so two models' costs are compared on tokens alone.
    /// </summary>
    public static async Task<LatencySummary> RunAsync(EvalSettings settings, GoldenSet set, int samples, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(log);
        var config = settings.Configuration(set);
        config["AI:CacheResponses"] = "false";
        var rows = new List<(TimeSpan Elapsed, long Input, long Output)>();
        await using (var host = EvalHost.Create(config, cache: null))
        {
            await host.IngestSamplesAsync();
            var rag = host.Get<RagAnswerService>();

            // One warm-up call: the first call loads the model (seconds for Ollama, more for a large ONNX model).
            var warm = set.Cases.First(c => c.AnswerEval && !c.ExpectDecline);
            await rag.AskAsync(warm.Question, set.Caller(warm.User));
            foreach (var c in set.Cases.Where(c => c.AnswerEval && !c.ExpectDecline && !c.IsPermissionCase).Take(samples))
            {
                var caller = set.Caller(c.User);
                using var scope = AiRequestContext.Begin("POST /ask", caller.UserId, caller.DisplayName);
                var watch = Stopwatch.StartNew();
                var answer = await rag.AskAsync(c.Question, caller);
                watch.Stop();
                if (scope.Context.ModelCalls == 0)
                {
                    continue;
                }

                rows.Add((watch.Elapsed, scope.Context.InputTokens, scope.Context.OutputTokens));
                log($"  {c.Id}: {watch.Elapsed.TotalSeconds:0.0} s, {scope.Context.InputTokens} in, {scope.Context.OutputTokens} out{(answer.Declined ? ", declined" : string.Empty)}");
            }
        }

        if (rows.Count == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0, 0, 0);
        }

        var seconds = rows.Select(r => r.Elapsed.TotalSeconds).Order().ToList();
        var input = rows.Average(r => (double)r.Input);
        var output = rows.Average(r => (double)r.Output);
        return new LatencySummary(
            rows.Count,
            Math.Round(Percentile(seconds, 0.50), 2),
            Math.Round(Percentile(seconds, 0.95), 2),
            Math.Round(input, 1),
            Math.Round(output, 1),
            Math.Round(rows.Sum(r => (double)r.Output) / rows.Sum(r => r.Elapsed.TotalSeconds), 2),
            Math.Round(OpsEvaluation.Price.Cost((long)Math.Round(input), (long)Math.Round(output)), 6));
    }

    internal static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>
/// Chapter 15: the side-by-side report of a current model and a candidate:
///   dotnet run --project eval/PriorAuthBot.Eval -- --compare current.json candidate.json [--out DIR]
/// Each metric gets the candidate's value, the change, and a verdict with the gate's own tolerances.
/// The report decides nothing on its own: the candidate's gate run does. It is the evidence a person
/// attaches to the upgrade's pull request.
/// </summary>
internal static class ModelComparison
{
    public static async Task<int> RunAsync(string[] args)
    {
        var i = Array.IndexOf(args, "--compare");
        if (i < 0 || i + 2 >= args.Length)
        {
            Console.Error.WriteLine("Usage: --compare <current eval-summary.json> <candidate eval-summary.json> [--out DIR] [--baselines FILE]");
            return 2;
        }

        var current = EvalSummary.Load(args[i + 1]);
        var candidate = EvalSummary.Load(args[i + 2]);
        var o = Array.IndexOf(args, "--out");
        var outDir = o >= 0 && o + 1 < args.Length ? args[o + 1] : Path.Combine(Directory.GetCurrentDirectory(), "eval-reports");
        var b = Array.IndexOf(args, "--baselines");
        var tolerance = Baselines.Load(b >= 0 && b + 1 < args.Length ? args[b + 1] : EvalPaths.Baselines).Tolerance;
        var lockPath = ModelLock.Find("models.lock.json");
        var report = Report(current, candidate, tolerance, lockPath is null ? null : ModelLock.Load(lockPath));
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "model-comparison.md"), report);
        Console.WriteLine(report);
        return 0;
    }

    public static string Report(EvalSummary current, EvalSummary candidate, Tolerances tolerance, ModelLock? locks = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(tolerance);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Model comparison: {current.Model} (current) and {candidate.Model} (candidate)\n\n");
        s.Append(CultureInfo.InvariantCulture, $"Embeddings: {current.EmbeddingModel}{(current.EmbeddingModel == candidate.EmbeddingModel ? " for both" : $" and {candidate.EmbeddingModel}: a different embedding model needs a new index (section 15.6)")}. Judge: {current.Judge ?? "none"}{(current.Judge == candidate.Judge ? string.Empty : $" and {candidate.Judge ?? "none"}: scores from different judges are not comparable")}.\n\n");
        s.Append("| Metric | Current | Candidate | Change | Verdict |\n|---|---:|---:|---:|---|\n");

        Row("Leaks (must be 0)", current.Leaks, candidate.Leaks, higherIsBetter: false, allowed: 0, absolute: true);
        Row("Recall@6", current.RecallAtTop, candidate.RecallAtTop, true, tolerance.Retrieval);
        Row("MRR", current.Mrr, candidate.Mrr, true, tolerance.Retrieval);
        Row("Declines kept", current.DeclinesKept, candidate.DeclinesKept, true, tolerance.Share);
        foreach (var metric in (current.Answers?.Keys ?? []).Union(candidate.Answers?.Keys ?? []).Order(StringComparer.Ordinal))
        {
            var share = metric == RegressionGate.ContainsExpected || metric.StartsWith("Citation", StringComparison.Ordinal);
            Row(metric, current.Answers?.GetValueOrDefault(metric), candidate.Answers?.GetValueOrDefault(metric), true, share ? tolerance.Share : tolerance.JudgeScore);
        }

        Row("Wrong declines", current.WrongDeclines, candidate.WrongDeclines, false, 2);
        Row("Tool selection", current.ToolSelection, candidate.ToolSelection, true, tolerance.Share);
        Row("Tool arguments", current.ToolArguments, candidate.ToolArguments, true, tolerance.Share);
        Row("Red-team breaches, absolute (must be 0)", current.RedTeamAbsoluteBreaches, candidate.RedTeamAbsoluteBreaches, false, 0, absolute: true);
        Row("Red-team breaches, measured", current.RedTeamMeasuredBreaches, candidate.RedTeamMeasuredBreaches, false, 0);
        if (current.Latency is { } cl && candidate.Latency is { } kl)
        {
            Info("Latency p50 (s)", cl.P50Seconds, kl.P50Seconds);
            Info("Latency p95 (s)", cl.P95Seconds, kl.P95Seconds);
            Info("Output tokens per second", cl.OutputTokensPerSecond, kl.OutputTokensPerSecond);
            Info("Input tokens per answer", cl.AverageInputTokens, kl.AverageInputTokens);
            Info("Output tokens per answer", cl.AverageOutputTokens, kl.AverageOutputTokens);
            Info("Cost per 1,000 answers at the shadow price (USD)", (double)cl.CostPerAnswer * 1000, (double)kl.CostPerAnswer * 1000);
        }

        s.Append('\n');
        s.Append(CultureInfo.InvariantCulture, $"Candidate's own gate against the current baselines: {(candidate.GateOk ? "passed" : "FAILED")}.\n");
        foreach (var failure in candidate.GateFailures)
        {
            s.Append("- ").Append(failure).Append('\n');
        }

        if (locks is not null)
        {
            s.Append("\nProvenance from models.lock.json:\n\n");
            foreach (var model in new[] { current.Model, candidate.Model }.Distinct(StringComparer.Ordinal))
            {
                var entry = locks.Models.FirstOrDefault(m => m.Id == model || OllamaModelCheck.OllamaName(m) == model);
                s.Append(entry is null
                    ? $"- {model}: NOT IN THE LOCK. Add it, with its license reviewed, before it can be approved.\n"
                    : $"- {model}: {entry.Source}{(entry.Path is null ? string.Empty : "/" + entry.Path)} at {Short(entry.Revision)}, {entry.License} (commercial use {(entry.CommercialUse ? "allowed" : "NOT allowed")}), {(entry.IsPinned ? "pinned" : "NOT PINNED")}{(entry.ReviewedBy is null ? string.Empty : $", reviewed by {entry.ReviewedBy.TrimEnd('.')}")}.\n");
            }
        }

        return s.ToString();

        void Row(string name, double? before, double? after, bool higherIsBetter, double allowed, bool absolute = false)
        {
            if (before is null && after is null)
            {
                return;
            }

            string verdict;
            if (after is null)
            {
                verdict = "not measured";
            }
            else if (absolute)
            {
                verdict = after.Value <= allowed ? "ok" : "FAIL";
            }
            else if (before is null)
            {
                verdict = "new";
            }
            else
            {
                var change = higherIsBetter ? after.Value - before.Value : before.Value - after.Value;
                verdict = change >= -allowed - 1e-9 ? (change > allowed ? "better" : "same, within tolerance") : "WORSE";
            }

            s.Append(CultureInfo.InvariantCulture, $"| {name} | {Format(before)} | {Format(after)} | {(before is null || after is null ? "" : Signed(after.Value - before.Value))} | {verdict} |\n");
        }

        void Info(string name, double before, double after) =>
            s.Append(CultureInfo.InvariantCulture, $"| {name} | {Format(before)} | {Format(after)} | {Signed(after - before)} | measured |\n");

        static string Format(double? value) => value is null ? "" : value.Value.ToString(Math.Abs(value.Value) < 0.01 && value.Value != 0 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

        static string Signed(double value) => (value >= 0 ? "+" : "") + value.ToString(Math.Abs(value) < 0.01 && value != 0 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

        static string Short(string? revision) => revision is { Length: > 12 } r ? r[..12] : revision ?? "no revision";
    }
}

/// <summary>
/// Chapter 15: checks every local model against models.lock.json, then exits:
///   dotnet run --project eval/PriorAuthBot.Eval -- --verify-models [--ollama NAME]... [--onnx DIR]
/// Ollama models are checked by digest against PRIORAUTHBOT_TEST_OLLAMA; an ONNX model folder by
/// file hashes. With --onnx and no --ollama, only the folder is checked. Exit code 1 means a model did not match, or is not in the lock. A license that does not
/// allow commercial use is reported; add --require-license (as production does) to fail on it too.
/// </summary>
internal static class VerifyModels
{
    public static async Task<int> RunAsync(string[] args)
    {
        var lockPath = ModelLock.Find("models.lock.json") ?? throw new FileNotFoundException("models.lock.json was not found.");
        var locks = ModelLock.Load(lockPath);
        var results = new List<ModelVerification>();
        var requireLicense = args.Contains("--require-license");
        var o = Array.IndexOf(args, "--onnx");

        // With --onnx alone, only that folder is checked: the job that runs it may have no Ollama yet.
        if (EvalSettings.OllamaEndpoint is { } endpoint && (o < 0 || args.Contains("--ollama")))
        {
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(30) };
            var wanted = args.Where((a, i) => i > 0 && args[i - 1] == "--ollama").ToHashSet(StringComparer.Ordinal);
            var models = locks.Models.Where(m => m.Kind == "Ollama" && (wanted.Count == 0 || wanted.Contains(OllamaModelCheck.OllamaName(m) ?? m.Id)));
            results.AddRange(await OllamaModelCheck.VerifyAsync(http, models, requireLicense));
        }

        if (o >= 0 && o + 1 < args.Length)
        {
            var id = EvalSettings.OnnxModelId;
            results.Add(locks.Get(id) is { } entry
                ? ModelIntegrity.Verify(entry, args[o + 1], requireLicense)
                : new ModelVerification(id, [$"{id} is not in models.lock.json."]));
        }

        foreach (var result in results)
        {
            var entry = locks.Get(result.ModelId);
            var license = entry is null ? string.Empty : $" [{entry.License}, commercial use {(entry.CommercialUse ? "allowed" : "NOT ALLOWED")}]";
            Console.WriteLine(result.Ok ? $"ok   {result.ModelId}{license}" : $"FAIL {result.ModelId}{license}: {string.Join(" ", result.Problems)}");
        }

        return results.All(r => r.Ok) ? 0 : 1;
    }
}
