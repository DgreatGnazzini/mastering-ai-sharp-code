using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PriorAuthBot.Core.Classification;

/// <summary>
/// The typed classification of a staff question (chapter 3). The model fills it in through
/// structured output; the attributes describe it to the model (in the JSON schema) and validate
/// what comes back.
/// </summary>
[Description("How PriorAuthBot classifies a staff question.")]
public sealed record QuestionIntent(
    [property: Description("The department whose documents would answer the question.")]
    [property: EnumDataType(typeof(Department))]
    Department Department,
    [property: Description("The most sensitive level of information needed to answer the question.")]
    [property: EnumDataType(typeof(Sensitivity))]
    Sensitivity Sensitivity,
    [property: Description("True if the question contains protected health information about an identifiable person.")]
    bool MentionsPhi) : IValidatableObject
{
    /// <summary>Rules that span more than one property. These run only after every property is valid.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MentionsPhi && Sensitivity < Sensitivity.Confidential)
        {
            yield return new ValidationResult(
                "A question that mentions PHI must be Confidential or Restricted.",
                [nameof(Sensitivity), nameof(MentionsPhi)]);
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<Department>))]
public enum Department
{
    [Description("Benefits, holidays, the code of conduct, and anything every employee can read.")]
    AllStaff,

    [Description("Clinical protocols, patient care, medications, and nursing procedures.")]
    Clinical,

    [Description("Claims, prior authorization, appeals, and member coverage.")]
    Claims,

    [Description("PTO, pay, hiring, leave, investigations, and other employment matters.")]
    HR,

    [Description("Privacy, HIPAA, incident reporting, audits, and regulatory obligations.")]
    Compliance,

    [Description("Laptops, accounts, passwords, remote access, and acceptable use of systems.")]
    IT,
}

[JsonConverter(typeof(JsonStringEnumConverter<Sensitivity>))]
public enum Sensitivity
{
    [Description("Could be published on the Deductible Dynamics website.")]
    Public,

    [Description("For any employee or contractor, but not for the public.")]
    Internal,

    [Description("Limited to a department or role, such as clinical protocols or claims procedures.")]
    Confidential,

    [Description("Named people only: investigations, layoffs, security incidents, or one person's PHI.")]
    Restricted,
}
