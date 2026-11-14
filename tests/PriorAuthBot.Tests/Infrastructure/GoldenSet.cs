using Xunit;
using System.Text.Json;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Tests.Infrastructure;

internal sealed record GoldenSet(
    string Description,
    Dictionary<string, string[]> Users,
    List<GoldenCase> Cases,
    GoldenSetThresholds Offline,
    GoldenSetThresholds Ollama)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<GoldenSet> Instance = new(() =>
        JsonSerializer.Deserialize<GoldenSet>(File.ReadAllText(TestPaths.GoldenSet), JsonOptions)
        ?? throw new InvalidOperationException("Golden set is empty."));

    public static GoldenSet Load() => Instance.Value;

    public CallerIdentity Caller(string user) => new($"golden-{user}", user, Users[user]);

    /// <summary>Case IDs for xUnit theories, so each question shows up as its own test.</summary>
    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in Load().Cases)
        {
            data.Add(c.Id);
        }

        return data;
    }

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
    string? Kind = null)
{
    /// <summary>Codes and policy numbers (chapter 8): vector search alone is not expected to find these.</summary>
    public bool IsExactTerm => Kind == "exact-term";

    public string[] Expected => ExpectedDocuments ?? [];

    public string[] Forbidden => MustNotSee ?? [];
}

/// <summary>The relevance cut-off depends on the embedding model, so each one has its own, measured with this set.</summary>
internal sealed record GoldenSetThresholds(double MinScore);
