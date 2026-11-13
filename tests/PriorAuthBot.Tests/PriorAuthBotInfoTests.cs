using PriorAuthBot.Core;
using Xunit;

namespace PriorAuthBot.Tests;

public class PriorAuthBotInfoTests
{
    [Fact]
    public void CurrentReportsChapterTag()
    {
        Assert.Matches("^ch[0-9]{2}$", PriorAuthBotInfo.Current.Chapter);
    }
}
