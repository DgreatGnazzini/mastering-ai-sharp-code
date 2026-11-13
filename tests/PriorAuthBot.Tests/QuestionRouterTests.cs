using PriorAuthBot.Core.Classification;
using Xunit;

namespace PriorAuthBot.Tests;

public class QuestionRouterTests
{
    [Fact]
    public void OrdinaryQuestionsAreAnswered() =>
        Assert.Equal(new QuestionRoute(RouteAction.Answer, Department.HR, null),
            QuestionRouter.Route(new(Department.HR, Sensitivity.Internal, false)));

    [Fact]
    public void PhiGetsANotice()
    {
        var route = QuestionRouter.Route(new(Department.Claims, Sensitivity.Confidential, true));

        Assert.Equal(RouteAction.AnswerWithPhiNotice, route.Action);
        Assert.Equal(QuestionRouter.PhiNotice, route.Notice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestrictedGoesToAPerson(bool mentionsPhi)
    {
        var route = QuestionRouter.Route(new(Department.Compliance, Sensitivity.Restricted, mentionsPhi));

        Assert.Equal(RouteAction.HumanReview, route.Action);
        Assert.Equal(Department.Compliance, route.Queue);
    }
}
