using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Prompts;

namespace PriorAuthBot.Core.Classification;

/// <summary>
/// Classifies a staff question into a <see cref="QuestionIntent"/> (chapter 3): a versioned system
/// prompt, few-shot examples as real user and assistant turns, structured output, validation, and
/// one retry that tells the model what was wrong.
/// </summary>
public sealed class QuestionClassifier(IChatClient chatClient)
{
    public const int MaxAttempts = 2;

    private static readonly Lazy<List<ChatMessage>> Preamble = new(() => BuildPreamble(includeExamples: true));
    private static readonly Lazy<List<ChatMessage>> ZeroShotPreamble = new(() => BuildPreamble(includeExamples: false));

    public Task<QuestionIntent> ClassifyAsync(string question, CancellationToken cancellationToken = default) =>
        chatClient.GetValidatedResponseAsync<QuestionIntent>(BuildMessages(question), CreateOptions(), MaxAttempts, cancellationToken);

    /// <summary>
    /// The exact messages sent for a question. Snapshot tests pin these down. Leaving out the
    /// few-shot examples is only for measuring what they add.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(string question, bool includeExamples = true)
    {
        ArgumentNullException.ThrowIfNull(question);
        return [.. (includeExamples ? Preamble : ZeroShotPreamble).Value, AsUserTurn(question)];
    }

    /// <summary>Temperature 0: the same question should get the same label every time.</summary>
    public static ChatOptions CreateOptions() => new() { Temperature = 0 };

    private static ChatMessage AsUserTurn(string question) =>
        new(ChatRole.User, $"<question>\n{question.Replace("</question>", string.Empty, StringComparison.OrdinalIgnoreCase)}\n</question>");

    private static List<ChatMessage> BuildPreamble(bool includeExamples)
    {
        var system = PromptLibrary.Render(PromptLibrary.ClassifyQuestion, new Dictionary<string, string>
        {
            ["departments"] = DescribeEnum<Department>(),
            ["sensitivities"] = DescribeEnum<Sensitivity>(),
        });

        List<ChatMessage> messages = [new(ChatRole.System, system)];
        if (!includeExamples)
        {
            return messages;
        }

        foreach (var example in PromptLibrary.GetExamples<ClassificationExample>(PromptLibrary.ClassifyQuestion, StructuredOutput.SerializerOptions))
        {
            messages.Add(AsUserTurn(example.Question));
            messages.Add(new(ChatRole.Assistant, JsonSerializer.Serialize(example.Intent, StructuredOutput.SerializerOptions)));
        }

        return messages;
    }

    /// <summary>Lists an enum's members with their [Description], so the prompt and the C# type cannot drift apart.</summary>
    private static string DescribeEnum<TEnum>()
        where TEnum : struct, Enum =>
        string.Join("\n", Enum.GetNames<TEnum>().Select(name =>
            $"- {name}: {typeof(TEnum).GetField(name)!.GetCustomAttribute<DescriptionAttribute>()?.Description}"));

    private sealed record ClassificationExample(string Question, QuestionIntent Intent);
}
