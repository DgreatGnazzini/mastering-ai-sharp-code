using System.Security.Claims;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Identity;

/// <summary>
/// Turns a validated token's claims into a <see cref="CallerIdentity"/> (chapter 7). Group claims
/// are trusted only because the token's signature, issuer, and audience were validated first by
/// the JWT bearer handler. Never read groups from a request body or header.
/// </summary>
public sealed class CallerIdentityFactory(IOptions<GroupMappingOptions> options)
{
    public const string GroupsClaim = "groups";

    public CallerIdentity Create(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("Retrieval requires an authenticated caller.");
        }

        // Entra ID omits the groups claim when the user is in more groups than fit in a token
        // (200 for JWTs) and adds _claim_names / hasgroups instead. A missing claim is not "no groups".
        if (principal.HasClaim("hasgroups", "true")
            || principal.FindAll("_claim_names").Any(c => c.Value.Contains("\"groups\"", StringComparison.Ordinal)))
        {
            throw new GroupOverageException();
        }

        var map = options.Value.Groups;
        var groups = principal.FindAll(GroupsClaim)
            .Select(c => map.TryGetValue(c.Value, out var name) ? name : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var userId = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("The token has no user identifier.");
        var name = principal.FindFirst("name")?.Value ?? principal.Identity.Name ?? userId;

        return new CallerIdentity(userId, name, groups);
    }
}
