using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 3 against a real local model: 16 labeled questions, classified with and without the
/// few-shot examples. Writes a table of accuracy and retries. Runs when PRIORAUTHBOT_TEST_OLLAMA is
/// set, as it is in the CI "RAG eval" job.
/// </summary>
public sealed class LiveClassifierTests(ITestOutputHelper output)
{
    private const double MinDepartmentAccuracy = 0.7;
    private const double MinPhiAccuracy = 0.85;

    [Fact]
    public async Task ClassifiesLabeledQuestionsWithOllama()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        var cases = JsonSerializer.Deserialize<List<LabeledQuestion>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "GoldenSet", "classification-set.json"), ct),
            StructuredOutput.SerializerOptions)!;

        using var ollama = new OllamaSharp.OllamaApiClient(
            new HttpClient { BaseAddress = new Uri(LiveSettings.Ollama!), Timeout = TimeSpan.FromMinutes(10) }, LiveSettings.ChatModel);
        var counter = new CountingChatClient(ollama);

        var summary = new StringBuilder("| Prompt | Department | Sensitivity | Mentions PHI | Model calls | Failed after retry |\n|---|---|---|---|---|---|\n");
        var detail = new StringBuilder("| Question | Expected | Few-shot | Zero-shot |\n|---|---|---|---|\n");
        var results = new Dictionary<bool, List<QuestionIntent?>>();

        foreach (var includeExamples in new[] { true, false })
        {
            counter.Calls = 0;
            var intents = new List<QuestionIntent?>();
            foreach (var labeled in cases)
            {
                try
                {
                    intents.Add(await counter.GetValidatedResponseAsync<QuestionIntent>(
                        QuestionClassifier.BuildMessages(labeled.Question, includeExamples), QuestionClassifier.CreateOptions(), QuestionClassifier.MaxAttempts, ct));
                }
                catch (StructuredOutputException ex)
                {
                    output.WriteLine($"{labeled.Question}: {ex.Message}");
                    intents.Add(null);
                }
            }

            results[includeExamples] = intents;
            summary.Append(CultureInfo.InvariantCulture,
                $"| {(includeExamples ? "Few-shot (4 examples)" : "Zero-shot")} | {Rate(cases, intents, (c, i) => c.Department == i.Department)} | {Rate(cases, intents, (c, i) => c.Sensitivity == i.Sensitivity)} | {Rate(cases, intents, (c, i) => c.MentionsPhi == i.MentionsPhi)} | {counter.Calls} | {intents.Count(i => i is null)} |\n");
        }

        for (var n = 0; n < cases.Count; n++)
        {
            var c = cases[n];
            detail.Append(CultureInfo.InvariantCulture,
                $"| {c.Question.Replace("|", "/", StringComparison.Ordinal)} | {Label(c.Department, c.Sensitivity, c.MentionsPhi)} | {Label(results[true][n])} | {Label(results[false][n])} |\n");
        }

        var report = $"Classifier: {LiveSettings.ChatModel}, {cases.Count} labeled questions.\n\n{summary}\n{detail}";
        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "classification-ollama.md"), report, ct);
        output.WriteLine(report);

        var fewShot = results[true];
        Assert.True(Accuracy(cases, fewShot, (c, i) => c.Department == i.Department) >= MinDepartmentAccuracy, "Department accuracy is below the bar.");
        Assert.True(Accuracy(cases, fewShot, (c, i) => c.MentionsPhi == i.MentionsPhi) >= MinPhiAccuracy, "PHI detection is below the bar.");
    }

    private static double Accuracy(List<LabeledQuestion> cases, List<QuestionIntent?> intents, Func<LabeledQuestion, QuestionIntent, bool> correct) =>
        cases.Zip(intents).Count(p => p.Second is not null && correct(p.First, p.Second)) / (double)cases.Count;

    private static string Rate(List<LabeledQuestion> cases, List<QuestionIntent?> intents, Func<LabeledQuestion, QuestionIntent, bool> correct) =>
        $"{cases.Zip(intents).Count(p => p.Second is not null && correct(p.First, p.Second))}/{cases.Count}";

    private static string Label(QuestionIntent? i) => i is null ? "invalid" : Label(i.Department, i.Sensitivity, i.MentionsPhi);

    private static string Label(Department d, Sensitivity s, bool phi) => $"{d}, {s}{(phi ? ", PHI" : string.Empty)}";

    private sealed record LabeledQuestion(string Question, Department Department, Sensitivity Sensitivity, bool MentionsPhi);

    /// <summary>Counts model calls, so the report shows how often validation forced a retry.</summary>
    private sealed class CountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public int Calls { get; set; }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
