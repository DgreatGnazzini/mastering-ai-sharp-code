using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace PriorAuthBot.Core.Intake;

/// <summary>
/// The fields of a prior authorization request form (chapter 4). Every field is nullable on
/// purpose: the extraction prompt tells the model to return null for anything it cannot read,
/// and a null is something a person can fix, where a confident guess is something nobody notices.
/// </summary>
[Description("The fields of a Deductible Dynamics prior authorization request form.")]
public sealed record IntakeForm(
    [property: Description("The member's full name as written on the form.")]
    string? MemberName,
    [property: Description("The member ID: DD- followed by seven digits.")]
    [property: RegularExpression(@"^DD-\d{7}$", ErrorMessage = "MemberId must be DD- followed by seven digits.")]
    string? MemberId,
    [property: Description("The member's date of birth, as yyyy-MM-dd.")]
    DateOnly? DateOfBirth,
    [property: Description("The service being requested, as written.")]
    string? RequestedService,
    [property: Description("The five-digit CPT procedure code.")]
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "CptCode must be five digits.")]
    string? CptCode,
    [property: Description("The ICD-10-CM diagnosis code, with its dot, such as M54.16.")]
    [property: RegularExpression(@"^[A-TV-Z][0-9][0-9A-Z](\.[0-9A-Z]{1,4})?$", ErrorMessage = "Icd10Code is not a valid ICD-10-CM code.")]
    string? Icd10Code,
    [property: Description("The ordering provider's name, without the NPI.")]
    string? OrderingProviderName,
    [property: Description("The ordering provider's ten-digit National Provider Identifier.")]
    [property: RegularExpression(@"^\d{10}$", ErrorMessage = "OrderingProviderNpi must be ten digits.")]
    string? OrderingProviderNpi,
    [property: Description("True if the Yes box for an urgent request is checked, false if No is checked, null if neither or both.")]
    bool? Urgent) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DateOfBirth is { } dob && (dob.Year < 1900 || dob > DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            yield return new ValidationResult("DateOfBirth must be after 1900 and not in the future.", [nameof(DateOfBirth)]);
        }
    }

    /// <summary>The names of the fields the model could not read.</summary>
    public IReadOnlyList<string> MissingFields()
    {
        List<string> missing = [];
        void Check(object? value, string name)
        {
            if (value is null || (value is string s && string.IsNullOrWhiteSpace(s)))
            {
                missing.Add(name);
            }
        }

        Check(MemberName, nameof(MemberName));
        Check(MemberId, nameof(MemberId));
        Check(DateOfBirth, nameof(DateOfBirth));
        Check(RequestedService, nameof(RequestedService));
        Check(CptCode, nameof(CptCode));
        Check(Icd10Code, nameof(Icd10Code));
        Check(OrderingProviderName, nameof(OrderingProviderName));
        Check(OrderingProviderNpi, nameof(OrderingProviderNpi));
        Check(Urgent, nameof(Urgent));
        return missing;
    }
}
