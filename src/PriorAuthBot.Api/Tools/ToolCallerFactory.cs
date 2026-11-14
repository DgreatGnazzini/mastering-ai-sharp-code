using System.Security.Claims;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Api.Tools;

/// <summary>
/// The caller a tool runs for (chapter 10): the identity from chapter 7, plus the user's own bearer
/// token, which the on-behalf-of flow exchanges. The token is read from the request that the JWT
/// handler already validated, and it never goes to the model or into a log.
/// </summary>
public sealed class ToolCallerFactory(CallerIdentityFactory identities)
{
    public ToolCaller Create(ClaimsPrincipal user, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        return new ToolCaller(identities.Create(user), token);
    }
}
