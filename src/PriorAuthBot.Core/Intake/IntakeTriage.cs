using System.Text.Json.Serialization;

namespace PriorAuthBot.Core.Intake;

[JsonConverter(typeof(JsonStringEnumConverter<IntakeRoute>))]
public enum IntakeRoute
{
    /// <summary>Every check passed; the request goes to the clinical review queue with its fields filled in.</summary>
    ReadyForClinicalReview,

    /// <summary>Something did not check out; a person corrects the fields before clinical review.</summary>
    NeedsDataEntryReview,
}

public sealed record IntakeResult(IntakeForm Form, IntakeRoute Route, IReadOnlyList<string> Problems, double? OcrConfidence);

/// <summary>
/// Decides what happens to an extracted form (chapter 4). Nothing here approves or denies a request:
/// that is a clinical decision made by a person. The only question is whether the data entry was
/// good enough to skip the person who would otherwise retype it. Everything that is not certain
/// goes to that person, with the reasons listed.
/// </summary>
public static class IntakeTriage
{
    /// <summary>Below this average word confidence, OCR output is not trusted without a look.</summary>
    public const double MinimumOcrConfidence = 0.90;

    public static IntakeResult Decide(IntakeForm form, MemberDirectory members, double? ocrConfidence = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(members);
        List<string> problems = [.. form.MissingFields().Select(f => $"{f} could not be read.")];

        var member = members.Find(form.MemberId);
        if (form.MemberId is not null && member is null)
        {
            problems.Add("MemberId does not match any member.");
        }
        else if (member is not null && form.DateOfBirth is { } dob && member.DateOfBirth != dob)
        {
            // The ID and the date of birth must agree. A wrong digit in either one points the request at the wrong person.
            problems.Add("DateOfBirth does not match the member on file.");
        }

        if (form.OrderingProviderNpi is not null && !Npi.IsValid(form.OrderingProviderNpi))
        {
            problems.Add("OrderingProviderNpi fails its check digit.");
        }

        if (ocrConfidence is < MinimumOcrConfidence)
        {
            problems.Add($"OCR confidence {ocrConfidence:0.00} is below {MinimumOcrConfidence:0.00}.");
        }

        return new IntakeResult(form, problems.Count == 0 ? IntakeRoute.ReadyForClinicalReview : IntakeRoute.NeedsDataEntryReview, problems, ocrConfidence);
    }
}
