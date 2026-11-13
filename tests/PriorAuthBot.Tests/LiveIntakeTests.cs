using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Intake;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 4 against a real local vision model: OCR of the scanned fax procedure, and field
/// extraction from the scanned intake form. Writes how much came back right, and how long it took,
/// to intake-ollama.md. Runs when PRIORAUTHBOT_TEST_OLLAMA is set.
/// </summary>
public sealed partial class LiveIntakeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReadsScansWithAVisionModel()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        await using var host = RagTestHost.Create(LiveSettings.OllamaConfiguration());
        var vision = host.Services.GetRequiredKeyedService<IChatClient>(AiServiceCollectionExtensions.VisionKey);
        var report = new StringBuilder();

        // 1. OCR: the scanned PDF through the same loader ingestion uses.
        var loader = new DocumentLoader(new VisionOcrService(vision));
        var watch = Stopwatch.StartNew();
        var scanned = await loader.LoadAsync(Path.Combine(TestPaths.SampleFiles, "fax-handling-procedure-scanned.pdf"), ct);
        var ocrTime = watch.Elapsed;
        var truth = await File.ReadAllTextAsync(Path.Combine(TestPaths.SampleFiles, "fax-handling-procedure-scanned.expected.txt"), ct);
        var (found, total, missed) = WordRecall(truth, scanned.Text);

        report.Append(CultureInfo.InvariantCulture, $"### OCR: scanned fax procedure ({LiveSettings.VisionModel})\n\n");
        report.Append(CultureInfo.InvariantCulture, $"| Words on the page | Words recovered | Recall | Time |\n|---|---|---|---|\n| {total} | {found} | {(double)found / total:P0} | {ocrTime.TotalSeconds:0.0} s |\n\n");
        report.Append(CultureInfo.InvariantCulture, $"Missed: {(missed.Count == 0 ? "none" : string.Join(", ", missed))}\n\n");

        // 2. Extraction: the intake form, field by field.
        var expected = JsonSerializer.Deserialize<IntakeForm>(await File.ReadAllTextAsync(Path.Combine(TestPaths.SampleIntake, "intake-form-001.expected.json"), ct), StructuredOutput.SerializerOptions)!;
        var image = BinaryData.FromBytes(await File.ReadAllBytesAsync(Path.Combine(TestPaths.SampleIntake, "intake-form-001.png"), ct));
        watch.Restart();
        IntakeForm? form = null;
        string? failure = null;
        try
        {
            form = await new IntakeFormExtractor(vision).ExtractFromImageAsync(image, "image/png", ct);
        }
        catch (StructuredOutputException ex)
        {
            failure = string.Join("; ", ex.Errors);
        }

        var extractTime = watch.Elapsed;
        report.Append(CultureInfo.InvariantCulture, $"### Extraction: intake form ({LiveSettings.VisionModel}, {extractTime.TotalSeconds:0.0} s)\n\n| Field | Expected | Extracted | Match |\n|---|---|---|---|\n");
        var correct = 0;
        foreach (var property in typeof(IntakeForm).GetProperties())
        {
            var want = Format(property.GetValue(expected));
            var got = form is null ? "(failed)" : Format(property.GetValue(form));
            var match = want == got;
            correct += match ? 1 : 0;
            report.Append(CultureInfo.InvariantCulture, $"| {property.Name} | {want} | {got} | {(match ? "yes" : "**no**")} |\n");
        }

        var fields = typeof(IntakeForm).GetProperties().Length;
        report.Append(CultureInfo.InvariantCulture, $"\n{correct} of {fields} fields correct.");
        if (form is not null)
        {
            var triage = IntakeTriage.Decide(form, MemberDirectory.Load(Path.Combine(TestPaths.SampleIntake, "members.json")));
            report.Append(CultureInfo.InvariantCulture, $" Route: {triage.Route}{(triage.Problems.Count > 0 ? " (" + string.Join("; ", triage.Problems) + ")" : string.Empty)}.\n");
        }
        else
        {
            report.Append(CultureInfo.InvariantCulture, $" Extraction failed validation twice: {failure}\n");
        }

        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "intake-ollama.md"), report.ToString(), ct);
        output.WriteLine(report.ToString());

        // The floor is low on purpose: this test records how a 3B vision model does, it does not grade it.
        Assert.True(found >= total / 2, $"OCR recovered only {found} of {total} words.");
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    /// <summary>How many of the page's words appear in the OCR output, as a multiset, ignoring case and punctuation.</summary>
    private static (int Found, int Total, List<string> Missed) WordRecall(string truth, string actual)
    {
        var remaining = Words(actual).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var expected = Words(truth).ToList();
        List<string> missed = [];
        foreach (var word in expected)
        {
            if (remaining.TryGetValue(word, out var count) && count > 0)
            {
                remaining[word] = count - 1;
            }
            else
            {
                missed.Add(word);
            }
        }

        return (expected.Count - missed.Count, expected.Count, missed);
    }

    private static IEnumerable<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value);

    [GeneratedRegex(@"[a-z0-9]+(?:-[a-z0-9]+)*")]
    private static partial Regex Word();
}
