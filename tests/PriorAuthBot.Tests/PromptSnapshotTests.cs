using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Snapshot tests for prompts (chapter 3). Each snapshot is everything the model receives for a
/// request: the messages, in order, and the JSON schema. A change to a prompt file, an example, an
/// enum description, or the QuestionIntent record changes the snapshot, so it cannot slip into a
/// pull request without a reviewer seeing exactly what the model will see.
/// </summary>
public class PromptSnapshotTests
{
    [Fact]
    public async Task ClassifyQuestion()
    {
        var model = new RecordingChatClient(_ => """{"department":"HR","sensitivity":"Internal","mentionsPhi":false}""");

        await new QuestionClassifier(model).ClassifyAsync("{question}", TestContext.Current.CancellationToken);

        Snapshot.Match(PromptLibrary.ClassifyQuestion, Describe(model.Requests[0], model.Options[0]));
    }

    [Fact]
    public void GroundedAnswer() =>
        Snapshot.Match(PromptLibrary.GroundedAnswer, PromptLibrary.Get(PromptLibrary.GroundedAnswer));

    private static string Describe(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var text = new StringBuilder();
        foreach (var message in messages)
        {
            text.Append("### ").Append(message.Role).Append('\n').Append(message.Text).Append("\n\n");
        }

        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema } json)
        {
            text.Append("### response format: ").Append(json.SchemaName).Append('\n')
                .Append(JsonSerializer.Serialize(schema, IndentedJson));
        }

        return text.ToString();
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
}
