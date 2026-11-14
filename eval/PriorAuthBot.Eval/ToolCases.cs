using System.Text.Json;

namespace PriorAuthBot.Eval;

/// <summary>Chapter 10's tool cases (eval/datasets/tool-cases.json).</summary>
internal sealed record ToolCaseSet(string Description, List<ToolCase> Cases)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ToolCaseSet Load() =>
        JsonSerializer.Deserialize<ToolCaseSet>(File.ReadAllText(EvalPaths.ToolCases), JsonOptions)
        ?? throw new InvalidOperationException($"{EvalPaths.ToolCases} is empty.");
}

internal sealed record ToolCase(
    string Id,
    string User,
    string Kind,
    string Question,
    string? ExpectTool = null,
    Dictionary<string, string>? ExpectArguments = null,
    bool ExpectApproval = false,
    string[]? AnswerMustContain = null,
    string[]? AnswerMustNotContain = null)
{
    public const string None = "none";

    /// <summary>Permission and injection cases are not scored on which tool was chosen, only on what must never happen.</summary>
    public bool ScoresSelection => Kind is not ("permission" or "injection");
}
