using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>Chapter 3: every way structured output goes wrong, and what the model hears back.</summary>
public class StructuredOutputTests
{
    [Theory]
    [InlineData("""{"department":"HR","sensitivity":"Internal","mentionsPhi":false}""", Department.HR)]
    [InlineData("""{"department":"hr","sensitivity":"internal","mentionsPhi":false}""", Department.HR)]
    public void AcceptsValidReplies(string reply, Department expected)
    {
        var errors = StructuredOutput.Check(Response(reply), out var intent);

        Assert.Empty(errors);
        Assert.Equal(expected, intent!.Department);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{}", "not valid JSON")]
    [InlineData("""{"department":"HR","mentionsPhi":false}""", "not valid JSON")]
    [InlineData("""{"department":"Finance","sensitivity":"Internal","mentionsPhi":false}""", "not valid JSON")]
    [InlineData("""{"department":42,"sensitivity":"Internal","mentionsPhi":false}""", "Department")]
    [InlineData("""{"department":"Claims","sensitivity":"Public","mentionsPhi":true}""", "must be Confidential or Restricted")]
    [InlineData("null", "not valid JSON")]
    [InlineData("```json\n{\"department\":\"Claims\",\"sensitivity\":\"Confidential\",\"mentionsPhi\":false}\n```", "not valid JSON")]
    public void RejectsInvalidReplies(string reply, string expectedError)
    {
        var errors = StructuredOutput.Check(Response(reply), out _);

        Assert.Contains(errors, e => e.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryShowsTheModelItsMistake()
    {
        var replies = new Queue<string>(
        [
            """{"department":"Claims","sensitivity":"Public","mentionsPhi":true}""",
            """{"department":"Claims","sensitivity":"Restricted","mentionsPhi":true}""",
        ]);
        var model = new RecordingChatClient(_ => replies.Dequeue());

        var intent = await model.GetValidatedResponseAsync<QuestionIntent>(
            [new(ChatRole.User, "classify")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Sensitivity.Restricted, intent.Sensitivity);
        var retry = model.Requests[1];
        Assert.Equal(ChatRole.Assistant, retry[^2].Role);
        Assert.Contains("must be Confidential or Restricted", retry[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorsNeverContainTheRawReply()
    {
        var model = new RecordingChatClient(_ => """{"department":"Member Jane Roe 1961-03-14","sensitivity":"Internal","mentionsPhi":false}""");

        var ex = await Assert.ThrowsAsync<StructuredOutputException>(() => model.GetValidatedResponseAsync<QuestionIntent>(
            [new(ChatRole.User, "classify")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Jane Roe", ex.Message, StringComparison.Ordinal);
    }

    private static ChatResponse<QuestionIntent> Response(string text) =>
        new(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)), StructuredOutput.SerializerOptions);
}
