using System.Text.RegularExpressions;
using PriorAuthBot.Eval;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Security;

/// <summary>
/// Chapter 12: the reviewer's first question is "does each threat map to a control and a test?"
/// These tests answer it on every push. A threat model that names a file someone renamed, or a test
/// someone deleted, fails here instead of going stale in a wiki.
/// </summary>
public sealed partial class ThreatModelTests
{
    private static readonly string ModelPath = Path.Combine(TestPaths.RepoRoot, "docs", "security", "threat-model.md");

    private static IReadOnlyList<(string Id, string Owasp, string Controls, string Tests)> Rows() =>
        [.. File.ReadAllLines(ModelPath)
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Where(c => c.Length >= 7 && ThreatId().IsMatch(c[1]))
            .Select(c => (c[1], c[3], c[4], c[5]))];

    [Fact]
    public void EveryThreatHasAnOwaspEntryAControlAndATest()
    {
        var rows = Rows();

        Assert.True(rows.Count >= 10, $"Expected the threat table in {ModelPath}.");
        Assert.All(rows, r =>
        {
            Assert.Matches(@"^LLM(0[1-9]|10)$", r.Owasp);
            Assert.NotEmpty(ControlFiles(r.Controls));
            Assert.NotEmpty(TestNames(r.Tests));
        });
    }

    [Fact]
    public void EveryControlFileExists()
    {
        foreach (var (id, _, controls, _) in Rows())
        {
            foreach (var file in ControlFiles(controls))
            {
                Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, file)), $"{id} names {file}, which does not exist.");
            }
        }
    }

    [Fact]
    public void EveryTestItNamesExists()
    {
        var cases = RedTeamCaseSet.Load().Cases.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var testClasses = typeof(ThreatModelTests).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, _, _, tests) in Rows())
        {
            foreach (var test in TestNames(tests))
            {
                Assert.True(cases.Contains(test) || testClasses.Contains(test), $"{id} names {test}, which is neither a red-team case nor a test class.");
            }
        }
    }

    [Fact]
    public void EveryRedTeamCaseTestsAThreat()
    {
        var named = Rows().SelectMany(r => TestNames(r.Tests)).ToHashSet(StringComparer.Ordinal);

        Assert.All(RedTeamCaseSet.Load().Cases, c => Assert.True(named.Contains(c.Id), $"{c.Id} is not listed against any threat in the threat model."));
    }

    private static List<string> ControlFiles(string cell) => [.. Backticked().Matches(cell).Select(m => m.Groups[1].Value)];

    private static List<string> TestNames(string cell) => [.. cell.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    [GeneratedRegex(@"^T\d+$")]
    private static partial Regex ThreatId();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Backticked();
}
