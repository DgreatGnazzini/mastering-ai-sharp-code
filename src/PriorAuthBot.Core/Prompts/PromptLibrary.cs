using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PriorAuthBot.Core.Prompts;

/// <summary>
/// Loads versioned prompt templates (chapter 3). Prompts live in Prompts/Templates as Markdown
/// files named <c>{name}.v{version}.md</c>, with optional few-shot examples beside them in
/// <c>{name}.v{version}.examples.json</c>. They are compiled into the assembly and change only
/// through a reviewed pull request, like any other source file. A changed prompt gets a new
/// version number, and the old file stays until nothing uses it.
/// </summary>
public static partial class PromptLibrary
{
    public const string ClassifyQuestion = "classify-question.v1";
    public const string GroundedAnswer = "grounded-answer.v1";
    public const string ExtractIntakeForm = "extract-intake-form.v1";

    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>Returns a prompt exactly as written, for prompts with no placeholders.</summary>
    public static string Get(string name) => Cache.GetOrAdd(name, n => Load($"{n}.md"));

    /// <summary>
    /// Fills the <c>{{placeholder}}</c> values in a prompt. A placeholder with no value, or a value
    /// with no placeholder, is an error: both mean the code and the prompt file disagree.
    /// </summary>
    public static string Render(string name, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var template = Get(name);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rendered = Placeholder().Replace(template, match =>
        {
            var key = match.Groups["key"].Value;
            if (!values.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"Prompt '{name}' has a placeholder '{{{{{key}}}}}' with no value.");
            }

            used.Add(key);
            return value;
        });

        var unused = values.Keys.Where(k => !used.Contains(k)).ToList();
        return unused.Count == 0
            ? rendered
            : throw new InvalidOperationException($"Prompt '{name}' has no placeholder for: {string.Join(", ", unused)}.");
    }

    /// <summary>Loads the few-shot examples that belong to a prompt.</summary>
    public static IReadOnlyList<T> GetExamples<T>(string name, JsonSerializerOptions serializerOptions) =>
        JsonSerializer.Deserialize<List<T>>(Load($"{name}.examples.json"), serializerOptions)
        ?? throw new InvalidOperationException($"Prompt '{name}' has an empty examples file.");

    private static string Load(string fileName)
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var resource = $"PriorAuthBot.Core.Prompts.Templates.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Prompt file '{fileName}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);

        // Git may check the file out with CRLF on Windows; the model should see the same text everywhere.
        return reader.ReadToEnd().ReplaceLineEndings("\n").Trim();
    }

    [GeneratedRegex(@"\{\{(?<key>[a-zA-Z][a-zA-Z0-9]*)\}\}")]
    private static partial Regex Placeholder();
}
