using PriorAuthBot.Core.Classification;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

public class QuestionClassifierTests
{
    [Fact]
    public async Task ParsesTypedIntent()
    {
        var model = new RecordingChatClient(_ => """{"department":"Clinical","sensitivity":"Confidential","mentionsPhi":false}""");

        var intent = await new QuestionClassifier(model).ClassifyAsync("When do I escalate a positive sepsis screen?", TestContext.Current.CancellationToken);

        Assert.Equal(new QuestionIntent(Department.Clinical, Sensitivity.Confidential, false), intent);
    }

    [Fact]
    public async Task RetriesOnceThenFails()
    {
        var model = new RecordingChatClient(_ => "not json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuestionClassifier(model).ClassifyAsync("Hello", TestContext.Current.CancellationToken));
        Assert.Equal(2, model.Requests.Count);
    }
}
