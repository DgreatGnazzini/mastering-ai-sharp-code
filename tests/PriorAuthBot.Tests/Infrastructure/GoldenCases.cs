global using GoldenCase = PriorAuthBot.Eval.GoldenCase;
global using GoldenSet = PriorAuthBot.Eval.GoldenSet;

using Xunit;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// The golden set lives in the eval project (eval/datasets/golden-set.json) from chapter 9 on.
/// These helpers turn its cases into xUnit theory data, so each question shows up as its own test.
/// </summary>
internal static class GoldenCases
{
    public static TheoryData<string> Ids(Func<GoldenCase, bool>? filter = null)
    {
        var data = new TheoryData<string>();
        foreach (var c in GoldenSet.Load().Cases.Where(c => filter?.Invoke(c) ?? true))
        {
            data.Add(c.Id);
        }

        return data;
    }
}
