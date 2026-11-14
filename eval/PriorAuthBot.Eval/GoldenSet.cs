using System.Text.Json;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Eval;

/// <summary>
/// The golden set (chapters 7 to 9): questions, who asks them, which documents should come back,
/// which must never come back for that person, and what the answer should say. It lives in
/// eval/datasets/golden-set.json, and every question and person in it is synthetic.
/// </summary>
internal sealed record GoldenSet(
    string Description,
    Dictionary<string, string[]> Users,
    List<GoldenCase> Cases,
    GoldenSetThresholds Offline,
    GoldenSetThresholds Ollama)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<GoldenSet> Default = new(() => Load(EvalPaths.GoldenSet));

    public static GoldenSet Load() => Default.Value;

    public static GoldenSet Load(string path) =>
        JsonSerializer.Deserialize<GoldenSet>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"{path} is empty.");

    public CallerIdentity Caller(string user) => new($"golden-{user}", user, Users[user]);

    public GoldenCase Case(string id) => Cases.Single(c => c.Id == id);
}

internal sealed record GoldenCase(
    string Id,
    string User,
    string Question,
    string[]? ExpectedDocuments,
    string[]? MustNotSee,
    bool ExpectDecline,
    string[]? AnswerMustContain,
    string? Kind = null,
    int Chapter = 7,
    string[]? AnswerMustNotContain = null,
    string? ExpectedAnswer = null,
    bool AnswerEval = false)
{
    public const string Fact = "fact";
    public const string ExactTerm = "exact-term";
    public const string Permission = "permission";
    public const string OutOfScope = "out-of-scope";

    /// <summary>Codes and policy numbers (chapter 8): vector search alone is not expected to find these.</summary>
    public bool IsExactTerm => Kind == ExactTerm;

    /// <summary>A "must not see" case (chapter 9): the question is about something this user may not read.</summary>
    public bool IsPermissionCase => Forbidden.Length > 0;

    public string[] Expected => ExpectedDocuments ?? [];

    public string[] Forbidden => MustNotSee ?? [];

    public string[] MustContain => AnswerMustContain ?? [];

    public string[] MustNotContain => AnswerMustNotContain ?? [];
}

/// <summary>The relevance cut-off depends on the embedding model, so each one has its own, measured with this set.</summary>
internal sealed record GoldenSetThresholds(double MinScore);
