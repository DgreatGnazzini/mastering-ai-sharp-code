using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Classification;
using PriorAuthBot.Core.Prompts;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Retrieval.Reranking;
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

    [Fact]
    public async Task RerankPassages()
    {
        var model = new RecordingChatClient(_ => """{"scores":[{"passage":1,"score":3},{"passage":2,"score":0}]}""");

        await new LlmReranker(model).ScoreAsync("{question}", ["{passage 1}", "{passage 2}"], TestContext.Current.CancellationToken);

        Snapshot.Match(PromptLibrary.RerankPassages, Describe(model.Requests[0], model.Options[0]));
    }

    [Fact]
    public async Task RewriteQuery()
    {
        var model = new RecordingChatClient(_ => """{"queries":["first","second","third"]}""");

        await new QueryRewriter(model).ExpandAsync("{question}", 3, TestContext.Current.CancellationToken);

        Snapshot.Match(PromptLibrary.RewriteQuery, Describe(model.Requests[0], model.Options[0]));
    }

    /// <summary>
    /// Chapter 10: a tool's name, description, and schema are prompt text too. The model reads them on
    /// every request, so they are snapshotted with the system prompt and reviewed like it.
    /// </summary>
    [Fact]
    public async Task AssistantTools()
    {
        var model = new ScriptedToolModel(ScriptedToolModel.Reply("Hello."));
        await using var hr = new MockHrFactory();
        await using var host = await ToolTestHost.CreateAsync(hr, model);

        await host.Get<Core.Tools.AssistantService>().AskAsync("{message}", new Core.Tools.ToolCaller(ToolTestHost.Nurse), TestContext.Current.CancellationToken);

        var text = new StringBuilder(Describe(model.Requests[0], model.Options[0]));
        foreach (var tool in model.Options[0]!.Tools!.OfType<AIFunctionDeclaration>())
        {
            text.Append("### tool: ").Append(tool.Name).Append(tool is ApprovalRequiredAIFunction ? " (requires approval)" : string.Empty).Append('\n')
                .Append(tool.Description).Append('\n')
                .Append(JsonSerializer.Serialize(tool.JsonSchema, IndentedJson)).Append("\n\n");
        }

        Snapshot.Match(PromptLibrary.AssistantTools, text.ToString());
    }

    /// <summary>
    /// Chapter 11: the policy agent. ChatClientAgent sends its instructions as ChatOptions.Instructions,
    /// not as a system message, so the snapshot includes them, with every tool the agent is given.
    /// </summary>
    [Fact]
    public async Task AgentPolicyAssistant()
    {
        var model = new ScriptedToolModel(ScriptedToolModel.Reply("Hello."));
        await using var hr = new MockHrFactory();
        await using var host = await AgentTestHost.CreateAsync(hr, model);

        await host.Get<Core.Agents.PolicyAgentService>().RunAsync("{message}", null, new Core.Tools.ToolCaller(ToolTestHost.Nurse), TestContext.Current.CancellationToken);

        var text = new StringBuilder(Describe(model.Requests[0], model.Options[0]));
        foreach (var tool in model.Options[0]!.Tools!.OfType<AIFunctionDeclaration>())
        {
            text.Append("### tool: ").Append(tool.Name).Append(tool is ApprovalRequiredAIFunction ? " (requires approval)" : string.Empty).Append('\n')
                .Append(tool.Description).Append("\n\n");
        }

        Snapshot.Match(PromptLibrary.AgentPolicyAssistant, text.ToString());
    }

    /// <summary>Chapter 11: the exception workflow's two model steps, as the model sees them.</summary>
    [Fact]
    public async Task ExceptionSteps()
    {
        var model = new ScriptedToolModel(
            ScriptedToolModel.Reply("""{"policyNumber":"HR-104","request":"Carry over 60 hours of PTO","requestedHours":60,"reason":"{reason}"}"""),
            ScriptedToolModel.Reply("{justification}"));
        await using var hr = new MockHrFactory();
        await using var host = await AgentTestHost.CreateAsync(hr, model);

        await host.Get<Core.Agents.Exceptions.ExceptionRequestService>().StartAsync("{message}", new Core.Tools.ToolCaller(ToolTestHost.Nurse), TestContext.Current.CancellationToken);

        Snapshot.Match(PromptLibrary.ExceptionUnderstand, Describe(model.Requests[0], model.Options[0]));
        Snapshot.Match(PromptLibrary.ExceptionDraft, Describe(model.Requests[1], model.Options[1]));
    }

    private static string Describe(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var text = new StringBuilder();
        if (options?.Instructions is { Length: > 0 } instructions)
        {
            text.Append("### instructions\n").Append(instructions).Append("\n\n");
        }

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
