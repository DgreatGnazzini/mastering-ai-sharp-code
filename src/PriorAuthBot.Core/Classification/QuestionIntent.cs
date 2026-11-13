using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PriorAuthBot.Core.Classification;

/// <summary>The typed classification of a staff question (chapter 3).</summary>
public sealed record QuestionIntent(
    [property: Required] Department Department,
    [property: Required] Sensitivity Sensitivity,
    bool MentionsPhi);

[JsonConverter(typeof(JsonStringEnumConverter<Department>))]
public enum Department
{
    AllStaff,
    Clinical,
    Claims,
    HR,
    Compliance,
    IT,
}

[JsonConverter(typeof(JsonStringEnumConverter<Sensitivity>))]
public enum Sensitivity
{
    Public,
    Internal,
    Confidential,
    Restricted,
}
