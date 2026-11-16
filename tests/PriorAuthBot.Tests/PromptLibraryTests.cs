using PriorAuthBot.Core.Prompts;
using Xunit;

namespace PriorAuthBot.Tests;

public class PromptLibraryTests
{
    [Theory]
    [InlineData(PromptLibrary.ClassifyQuestion)]
    [InlineData(PromptLibrary.GroundedAnswer)]
    public void EveryPromptLoadsWithUnixLineEndings(string name)
    {
        var prompt = PromptLibrary.Get(name);

        Assert.NotEmpty(prompt);
        Assert.DoesNotContain('\r', prompt);
    }

    [Fact]
    public void MissingValueIsAnError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PromptLibrary.Render(PromptLibrary.ClassifyQuestion, new Dictionary<string, string> { ["departments"] = "x" }));

        Assert.Contains("{{sensitivities}}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusedValueIsAnError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PromptLibrary.Render(PromptLibrary.GroundedAnswer, new Dictionary<string, string> { ["tone"] = "friendly" }));

        Assert.Contains("tone", ex.Message, StringComparison.Ordinal);
    }
}
