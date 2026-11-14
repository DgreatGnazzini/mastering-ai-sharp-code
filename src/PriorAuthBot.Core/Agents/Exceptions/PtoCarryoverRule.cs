using System.Globalization;

namespace PriorAuthBot.Core.Agents.Exceptions;

/// <summary>
/// The one exception PriorAuthBot can file in this chapter: carrying over more PTO than HR-104
/// allows. The rule is code, read against the facts step 2 looked up. The policy text says "up to 40
/// hours" and "before December 1"; a test checks the sample policy still says so, so a policy edit
/// that changes the rule fails the build until a person updates this class.
/// </summary>
public static class PtoCarryoverRule
{
    public const string PolicyNumber = "HR-104";

    /// <summary>The month and day the policy's exception deadline falls on, in the year being carried over from.</summary>
    public static readonly (int Month, int Day) DeadlineDate = (12, 1);

    public static PolicyCheck Check(ExceptionCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.PolicyNumber is null)
        {
            return Stop(c, StopCodes.PolicyNotFound, "I could not find a policy you can read that this request is about.");
        }

        if (c.PolicyNumber != PolicyNumber)
        {
            return Stop(c, StopCodes.NotSupported, $"I can only file PTO carryover exceptions to {PolicyNumber} for now. For {c.PolicyNumber}, ask your manager directly.");
        }

        if (c.AvailableHours is not { } available || c.CarryoverLimitHours is not { } limit)
        {
            return Stop(c, StopCodes.NoPtoRecord, "The HR system has no PTO record for you, so there is nothing to carry over. Contractors and temporary staff do not accrue PTO.");
        }

        if (c.Request.RequestedHours is not { } requested || requested <= 0)
        {
            return Stop(c, StopCodes.NoHoursGiven, "How many hours do you want to carry over in total? Ask again with the number of hours.");
        }

        var deadline = new DateOnly(c.Today.Year, DeadlineDate.Month, DeadlineDate.Day);
        if (requested <= limit)
        {
            return Stop(c, StopCodes.NotNeeded, Invariant($"No exception is needed: {PolicyNumber} already lets you carry over up to {limit:0.##} hours, and you asked for {requested:0.##}."), deadline: deadline);
        }

        if (requested > available)
        {
            return Stop(c, StopCodes.OverBalance, Invariant($"You asked to carry over {requested:0.##} hours, but your balance is {available:0.##} hours."), deadline: deadline);
        }

        if (c.Today >= deadline)
        {
            return Stop(c, StopCodes.PastDeadline, Invariant($"{PolicyNumber} needs a manager's written approval before {deadline:MMMM d}. That date has passed for this year."), deadline: deadline);
        }

        if (c.Approver is null || c.Approver.UserId == c.RequesterId)
        {
            return Stop(c, StopCodes.NoApprover, "The HR system has no manager on record for you, so there is no one to send this to. Contact HR.", deadline: deadline);
        }

        return new PolicyCheck(c, CanRequest: true, StopCode: null,
            Invariant($"{requested:0.##} hours is {requested - limit:0.##} above the {limit:0.##}-hour limit, within your balance of {available:0.##}, before the {deadline:MMMM d} deadline. {c.Approver.Name} approves it."),
            HoursAboveLimit: requested - limit, Deadline: deadline);
    }

    private static PolicyCheck Stop(ExceptionCase c, string code, string explanation, DateOnly? deadline = null) =>
        new(c, CanRequest: false, code, explanation, 0, deadline);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
