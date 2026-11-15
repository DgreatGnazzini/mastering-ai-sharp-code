using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace PriorAuthBot.Core.Agents.Exceptions;

/// <summary>What the requester typed, and nothing else. Who they are comes from the workflow's context, not the message.</summary>
public sealed record ExceptionRequestInput(string Message);

/// <summary>
/// Step 1's output: the request, as the model understood it (structured output, chapter 3). Every
/// field is something the requester said. Nothing here is trusted as a fact about them: step 2
/// looks the facts up as the requester.
/// </summary>
[Description("A staff member's request for an exception to a Deductible Dynamics policy.")]
public sealed record ExceptionRequestFacts(
    [property: Description("The policy number the request names, such as HR-104, or null if it names none.")]
    [property: StringLength(16)]
    string? PolicyNumber,
    [property: Description("What the staff member is asking for, in one short sentence.")]
    [property: Required, StringLength(200, MinimumLength = 3)]
    string Request,
    [property: Description("For a PTO carryover request: the total number of PTO hours they want to carry into next year. Null if they did not say.")]
    [property: Range(0, 2000)]
    double? RequestedHours,
    [property: Description("Why they are asking, in their own words, in at most two sentences.")]
    [property: Required, StringLength(400, MinimumLength = 3)]
    string Reason);

/// <summary>The approver, as the HR system names them.</summary>
public sealed record Approver(string UserId, string Name);

/// <summary>
/// Step 2's output: the facts, looked up in code as the requester. The numbers come from the HR
/// system and the rule from the policy document, never from the model.
/// </summary>
public sealed record ExceptionCase(
    string RequesterId,
    string RequesterName,
    ExceptionRequestFacts Request,
    string? PolicyNumber,
    string? PolicyTitle,
    string? PolicyLink,
    string? ExceptionRule,
    double? AvailableHours,
    double? CarryoverLimitHours,
    Approver? Approver,
    DateOnly Today);

/// <summary>Step 3's output: whether the request can go to an approver, and if not, why.</summary>
public sealed record PolicyCheck(ExceptionCase Case, bool CanRequest, string? StopCode, string Explanation, double HoursAboveLimit, DateOnly? Deadline);

/// <summary>
/// Step 4's output: the request as the approver will read it. The fields above <see cref="Justification"/>
/// are written by code from the facts; only the justification is the model's, from the requester's words.
/// </summary>
public sealed record ExceptionDraft(
    string Subject,
    string PolicyNumber,
    string PolicyTitle,
    string Rule,
    double RequestedHours,
    double AvailableHours,
    double CarryoverLimitHours,
    double HoursAboveLimit,
    DateOnly Deadline,
    string Justification,
    string RequesterId,
    string RequesterName,
    Approver Approver);

/// <summary>The requester's answer at the first human step: send the draft to the approver, or not.</summary>
public sealed record RequesterAnswer(bool Submit);

/// <summary>What the approver is asked to decide.</summary>
public sealed record ApproverReview(ExceptionDraft Draft);

/// <summary>The approver's answer at the second human step.</summary>
public sealed record ApproverAnswer(bool Approve, string? Comment, string DecidedBy);

/// <summary>How a run ended, or where it is waiting.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ExceptionStatus>))]
public enum ExceptionStatus
{
    Running,
    WaitingForRequester,
    WaitingForApprover,
    Approved,
    Denied,
    Withdrawn,
    Stopped,
    Expired,
    Failed,
}

/// <summary>The workflow's output: the end state of one request.</summary>
public sealed record ExceptionOutcome(ExceptionStatus Status, string? StopCode, string Message, string? DecisionId = null);

/// <summary>Why the workflow stopped without asking anyone to decide. Each is decided in code.</summary>
public static class StopCodes
{
    public const string NotUnderstood = "not-understood";
    public const string NotSupported = "not-supported";
    public const string PolicyNotFound = "policy-not-found";
    public const string NoPtoRecord = "no-pto-record";
    public const string NoHoursGiven = "no-hours";
    public const string NotNeeded = "not-needed";
    public const string OverBalance = "over-balance";
    public const string PastDeadline = "past-deadline";
    public const string NoApprover = "no-approver";
    public const string Limit = "limit";

    /// <summary>Chapter 12: the guardrails refused the request text.</summary>
    public const string Blocked = "blocked";
}
