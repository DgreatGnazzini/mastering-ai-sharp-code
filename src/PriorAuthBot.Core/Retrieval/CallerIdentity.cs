namespace PriorAuthBot.Core.Retrieval;

/// <summary>
/// The signed-in user a search runs for. Retrieval has no overload without one: there is no way
/// to search "as the app" (chapter 6).
/// </summary>
public sealed record CallerIdentity(string UserId, string DisplayName, IReadOnlyList<string> Groups)
{
    public bool IsInAnyGroup(IEnumerable<string> allowedGroups) => allowedGroups.Any(Groups.Contains);
}
