using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PriorAuthBot.Eval;

/// <summary>
/// eval/baselines.json: the last accepted numbers for the shipped configuration, per provider.
/// A change to this file lowers the bar for every later pull request, so CODEOWNERS sends it to
/// the people who own PriorAuthBot's quality, not just the person whose change needs it lowered.
/// Leaks are not in it: no file can make a leak acceptable.
/// </summary>
internal sealed record Baselines(
    string? Description,
    Tolerances Tolerance,
    Dictionary<string, ProviderBaseline> Providers)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Baselines Load(string path) =>
        JsonSerializer.Deserialize<Baselines>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException($"{path} is empty.");

    public ProviderBaseline? For(string provider) => Providers.GetValueOrDefault(provider);
}

/// <summary>
/// How far a metric may fall below its baseline before the gate fails. Judges are noisy, so their
/// 1 to 5 scores get more room than retrieval, which is deterministic for a given model.
/// </summary>
internal sealed record Tolerances(double Retrieval = 0.02, double JudgeScore = 0.25, double Share = 0.05);

internal sealed record ProviderBaseline(RetrievalBaseline? Retrieval, Dictionary<string, double>? Answers);

internal sealed record RetrievalBaseline(double RecallAtTop, double Mrr, double DeclinesKept);

internal sealed record GateResult(IReadOnlyList<string> Passed, IReadOnlyList<string> Failed)
{
    public bool Ok => Failed.Count == 0;
}

internal static class RegressionGate
{
    public const string ContainsExpected = "Contains expected fact";

    public static GateResult Check(
        Baselines baselines,
        string provider,
        RetrievalSummary retrieval,
        int retrievalLeaks,
        AnswerSummary? answers)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(retrieval);
        var passed = new List<string>();
        var failed = new List<string>();
        var tolerance = baselines.Tolerance;

        // The absolute rule first: one leak fails the run, in any configuration, whatever the baseline.
        Record(retrievalLeaks == 0, $"Leaks in retrieval: {retrievalLeaks} (must be 0)");
        if (answers is not null)
        {
            Record(answers.Leaks == 0, $"Leaks in prompts and answers: {answers.Leaks} (must be 0)");
        }

        var baseline = baselines.For(provider);
        if (baseline?.Retrieval is { } r)
        {
            AtLeast("Recall@6", retrieval.RecallAtTop, r.RecallAtTop, tolerance.Retrieval);
            AtLeast("MRR", retrieval.Mrr, r.Mrr, tolerance.Retrieval);
            AtLeast("Declines kept", retrieval.Declines == 0 ? 1 : (double)retrieval.DeclinesKept / retrieval.Declines, r.DeclinesKept, tolerance.Share);
        }
        else
        {
            passed.Add($"No retrieval baseline for {provider} yet: nothing to compare.");
        }

        if (answers is not null && baseline?.Answers is { } a)
        {
            foreach (var (metric, floor) in a)
            {
                if (metric == ContainsExpected)
                {
                    AtLeast(metric, answers.WithExpectedFact == 0 ? 1 : (double)answers.ContainsExpected / answers.WithExpectedFact, floor, tolerance.Share);
                }
                else if (answers.Averages.TryGetValue(metric, out var value))
                {
                    AtLeast(metric, value, floor, metric.StartsWith("Citation", StringComparison.Ordinal) ? tolerance.Share : tolerance.JudgeScore);
                }
                else
                {
                    Record(false, $"{metric}: no judge produced a score (baseline {floor:0.00})");
                }
            }
        }

        return new GateResult(passed, failed);

        void AtLeast(string name, double value, double floor, double allowed) =>
            Record(value >= floor - allowed - 1e-9, string.Create(CultureInfo.InvariantCulture, $"{name}: {value:0.00} (baseline {floor:0.00}, tolerance {allowed:0.00})"));

        void Record(bool ok, string line) => (ok ? passed : failed).Add(line);
    }

    /// <summary>The numbers a person would commit as the new baseline, if they accept this run.</summary>
    public static ProviderBaseline Proposed(RetrievalSummary retrieval, AnswerSummary? answers)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        Dictionary<string, double>? scores = null;
        if (answers is not null)
        {
            scores = answers.Averages
                .Where(kv => kv.Key != CitationFaithfulnessEvaluator.CoverageMetricName)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2));
            if (answers.WithExpectedFact > 0)
            {
                scores[ContainsExpected] = Math.Round((double)answers.ContainsExpected / answers.WithExpectedFact, 2);
            }
        }

        return new ProviderBaseline(
            new RetrievalBaseline(
                Math.Round(retrieval.RecallAtTop, 2),
                Math.Round(retrieval.Mrr, 2),
                Math.Round(retrieval.Declines == 0 ? 1 : (double)retrieval.DeclinesKept / retrieval.Declines, 2)),
            scores);
    }
}
