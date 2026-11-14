using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using PriorAuthBot.Core.Rag;

namespace PriorAuthBot.Tests.Evaluation;

/// <summary>The numbered sources an answer was given, for <see cref="CitationFaithfulnessEvaluator"/>.</summary>
public sealed class CitationSourcesContext(IReadOnlyList<string> sources)
    : EvaluationContext(ContextName, sources.Select((s, i) => (AIContent)new TextContent($"[{i + 1}] {s}")).ToList())
{
    public const string ContextName = "Cited sources";

    public IReadOnlyList<string> Sources { get; } = sources;
}

/// <summary>
/// Citation faithfulness (chapter 8). Groundedness asks whether the answer is supported by the
/// sources as a whole; this asks whether each sentence is supported by the source it cites. An
/// answer can be perfectly grounded and still cite [2] for a fact that is in [1], and a reader who
/// clicks [2] then finds nothing. Two metrics: faithfulness, the share of cited sentences that their
/// cited source supports, and coverage, the share of sentences that cite anything at all.
/// One judge call per answer, with every cited sentence in it.
/// </summary>
public sealed class CitationFaithfulnessEvaluator : IEvaluator
{
    public const string FaithfulnessMetricName = "Citation faithfulness";
    public const string CoverageMetricName = "Citation coverage";

    private const string Instructions = """
        You check citations in an answer written from numbered sources.

        For each numbered claim, decide whether the cited source text, on its own, states the claim.
        A claim is supported only if the cited source says it. A claim that is true but stated only in a
        different source is not supported. Paraphrase is fine; added facts are not.

        Reply with JSON only, such as {"verdicts": [{"claim": 1, "supported": true}, {"claim": 2, "supported": false}]}.
        """;

    public IReadOnlyCollection<string> EvaluationMetricNames => [FaithfulnessMetricName, CoverageMetricName];

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelResponse);
        var faithfulness = new NumericMetric(FaithfulnessMetricName);
        var coverage = new NumericMetric(CoverageMetricName);
        var result = new EvaluationResult(faithfulness, coverage);

        var sources = additionalContext?.OfType<CitationSourcesContext>().FirstOrDefault()?.Sources;
        if (sources is null || chatConfiguration is null)
        {
            faithfulness.AddDiagnostics(EvaluationDiagnostic.Error("Needs a judge and a CitationSourcesContext."));
            return result;
        }

        var sentences = Citations.Sentences(modelResponse.Text);
        coverage.Value = sentences.Count == 0 ? 0 : (double)sentences.Count(s => s.Sources.Count > 0) / sentences.Count;

        // Each (sentence, cited source) pair is one claim to check.
        var claims = sentences
            .SelectMany(s => s.Sources.Where(n => n >= 1 && n <= sources.Count).Select(n => (s.Text, Source: n)))
            .ToList();
        if (claims.Count == 0)
        {
            faithfulness.AddDiagnostics(EvaluationDiagnostic.Warning("The answer cites no source."));
            return result;
        }

        var prompt = new StringBuilder();
        for (var i = 0; i < claims.Count; i++)
        {
            prompt.Append(CultureInfo.InvariantCulture, $"Claim {i + 1}: {claims[i].Text}\nCited source [{claims[i].Source}]: {sources[claims[i].Source - 1]}\n\n");
        }

        var response = await chatConfiguration.ChatClient.GetResponseAsync<Verdicts>(
            [new(ChatRole.System, Instructions), new(ChatRole.User, prompt.ToString().TrimEnd())],
            new ChatOptions { Temperature = 0 },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!response.TryGetResult(out var verdicts) || verdicts?.List is not { Count: > 0 } list)
        {
            faithfulness.AddDiagnostics(EvaluationDiagnostic.Error($"The judge's reply could not be read: {response.Text}"));
            return result;
        }

        var supported = list.Where(v => v.Claim >= 1 && v.Claim <= claims.Count && v.Supported).Select(v => v.Claim).Distinct().Count();
        faithfulness.Value = (double)supported / claims.Count;
        faithfulness.Reason = $"{supported} of {claims.Count} cited claims are supported by the source they cite.";
        return result;
    }

    private sealed record Verdicts([property: System.Text.Json.Serialization.JsonPropertyName("verdicts")] List<Verdict> List);

    private sealed record Verdict(int Claim, bool Supported);
}
