using System.Collections.Concurrent;
using System.Reflection;

namespace PriorAuthBot.Core.Prompts;

/// <summary>
/// Loads versioned prompt templates (chapter 3). Prompts live in Prompts/Templates as Markdown
/// files named <c>{name}.v{version}.md</c>, are compiled into the assembly, and change only through
/// a reviewed pull request, like any other source file.
/// </summary>
public static class PromptLibrary
{
    public const string ClassifyQuestion = "classify-question.v1";
    public const string GroundedAnswer = "grounded-answer.v1";

    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Get(string name) => Cache.GetOrAdd(name, Load);

    private static string Load(string name)
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var resource = $"PriorAuthBot.Core.Prompts.Templates.{name}.md";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Prompt '{name}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
