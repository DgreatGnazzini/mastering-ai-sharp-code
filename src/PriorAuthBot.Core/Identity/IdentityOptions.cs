namespace PriorAuthBot.Core.Identity;

public sealed class GroupMappingOptions
{
    public const string SectionName = "Identity";

    /// <summary>
    /// Maps Entra ID group object IDs (what the token's "groups" claim carries) to the group names
    /// used in document ACLs, such as <c>"5f1c...": "Clinical"</c>. Unknown IDs are dropped.
    /// </summary>
    public Dictionary<string, string> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
