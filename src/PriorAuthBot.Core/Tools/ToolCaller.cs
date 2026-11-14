using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Tools;

/// <summary>
/// The signed-in user a tool runs for (chapter 10). Every tool is built for one caller, per request,
/// so no tool exists that could act as "the app". <paramref name="AccessToken"/> is the user's own
/// token for PriorAuthBot, which the on-behalf-of flow exchanges for a token to a downstream API.
/// It is null with demo sign-in, where the development token provider stands in for Entra ID.
/// </summary>
public sealed record ToolCaller(CallerIdentity Identity, string? AccessToken = null)
{
    public string UserId => Identity.UserId;

    /// <summary>Never print the token, even in a debugger or a log line that formats the record.</summary>
    public override string ToString() => $"ToolCaller {{ UserId = {UserId} }}";
}
