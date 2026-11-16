using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Api.Auth;

/// <summary>
/// Development only. Signs a request in as one of the demo personas named in the
/// <c>X-Demo-User</c> header, with the same claim shapes Entra ID puts in a token ("oid", "name",
/// and "groups" holding group object IDs). The rest of the pipeline cannot tell the difference,
/// which is the point: the code that turns claims into a filter is the code that runs in production.
/// Program.cs refuses to start with this scheme outside the Development environment.
/// </summary>
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Demo";
    public const string HeaderName = "X-Demo-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var persona = configuration.GetSection($"Auth:DemoUsers:{user}");
        if (!persona.Exists())
        {
            return Task.FromResult(AuthenticateResult.Fail($"Unknown demo user '{user}'."));
        }

        List<Claim> claims =
        [
            new("oid", $"demo-{user}"),
            new("name", persona["Name"] ?? user),
        ];
        claims.AddRange(persona.GetSection("Groups").Get<string[]>()?.Select(g => new Claim("groups", g)) ?? []);
        if (persona.GetValue<bool>("GroupOverage"))
        {
            claims.Add(new Claim("hasgroups", "true"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
