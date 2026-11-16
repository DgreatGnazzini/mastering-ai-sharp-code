using System.Text.Json.Serialization;

namespace PriorAuthBot.Core.Classification;

[JsonConverter(typeof(JsonStringEnumConverter<RouteAction>))]
public enum RouteAction
{
    /// <summary>PriorAuthBot answers from the documents the caller can see.</summary>
    Answer,

    /// <summary>PriorAuthBot answers, and reminds the caller not to paste PHI into it.</summary>
    AnswerWithPhiNotice,

    /// <summary>A person in the department's queue answers instead of the model.</summary>
    HumanReview,
}

/// <summary>Where a question goes, and what PriorAuthBot tells the caller about it.</summary>
public sealed record QuestionRoute(RouteAction Action, Department Queue, string? Notice);

/// <summary>
/// Turns a <see cref="QuestionIntent"/> into a route (chapter 3). The rules are plain C#, not
/// a prompt: the model labels the question, and code you can test decides what happens.
/// </summary>
public static class QuestionRouter
{
    public const string PhiNotice =
        "Please don't include patient or member details. PriorAuthBot answers policy questions and doesn't need them.";

    public static QuestionRoute Route(QuestionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent switch
        {
            { Sensitivity: Sensitivity.Restricted } => new(RouteAction.HumanReview, intent.Department,
                $"This question needs a person. It has been sent to the {intent.Department} team."),
            { MentionsPhi: true } => new(RouteAction.AnswerWithPhiNotice, intent.Department, PhiNotice),
            _ => new(RouteAction.Answer, intent.Department, null),
        };
    }
}
