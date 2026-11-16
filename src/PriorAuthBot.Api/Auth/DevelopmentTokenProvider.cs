using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Api.Auth;

/// <summary>
/// Development only, the stand-in for Entra ID's on-behalf-of flow (chapter 10). With demo sign-in
/// there is no user token to exchange, so this signs a short-lived token for the mock HR API that
/// has the same shape as the real one: the user's oid, the delegated scope, and PriorAuthBot as the client.
/// Program.cs registers it only in the Development environment, with demo sign-in.
/// </summary>
public sealed class DevelopmentTokenProvider(IOptions<DevelopmentTokenOptions> options, TimeProvider time) : IDownstreamTokenProvider
{
    public ValueTask<string> GetTokenAsync(ToolCaller caller, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(scopes);
        var settings = options.Value;
        var now = time.GetUtcNow().UtcDateTime;

        // "api://dd-hr/Pto.Read" becomes audience "api://dd-hr" and scope "Pto.Read", as Entra issues it.
        var audience = scopes.Select(s => s[..s.LastIndexOf('/')]).Distinct(StringComparer.Ordinal).Single();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Subject = new ClaimsIdentity(
            [
                new Claim("oid", caller.UserId),
                new Claim("name", caller.Identity.DisplayName),
                new Claim("scp", string.Join(' ', scopes.Select(s => s[(s.LastIndexOf('/') + 1)..]))),
                new Claim("azp", settings.ClientId),
            ]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey ?? throw new InvalidOperationException("Tools:DevelopmentTokens:SigningKey is not set."))),
                SecurityAlgorithms.HmacSha256),
        });
        return ValueTask.FromResult(token);
    }
}

public sealed class DevelopmentTokenOptions
{
    public const string SectionName = "Tools:DevelopmentTokens";

    public string Issuer { get; set; } = "priorauthbot-development";

    public string ClientId { get; set; } = "priorauthbot-development";

    public string? SigningKey { get; set; }
}
