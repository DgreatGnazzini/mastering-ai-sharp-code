using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PriorAuthBot.Core.Tools;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Tools;

/// <summary>
/// The HR API's side of on-behalf-of (chapter 10): it serves a user's own balance to a delegated
/// token for that user, and nothing to PriorAuthBot acting as itself.
/// </summary>
public sealed class OnBehalfOfTests(MockHrFactory hr) : IClassFixture<MockHrFactory>
{
    private static readonly string[] PtoRead = ["api://dd-hr/Pto.Read"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADelegatedTokenReadsThatUsersBalance()
    {
        var token = await MockHrFactory.Tokens().GetTokenAsync(new ToolCaller(ToolTestHost.Nurse), PtoRead, Ct);

        var response = await GetAsync("me/pto-balance", token);

        response.EnsureSuccessStatusCode();
        var balance = await response.Content.ReadFromJsonAsync<PtoBalance>(Ct);
        Assert.Equal("Nina Okafor, RN", balance!.EmployeeName);
        Assert.Equal(86.5, balance.AvailableHours);
    }

    [Fact]
    public async Task TheTokenDecidesWhoseBalanceNotTheRequest()
    {
        var token = await MockHrFactory.Tokens().GetTokenAsync(new ToolCaller(ToolTestHost.Nurse), PtoRead, Ct);

        // Asking for someone else in the query string changes nothing: there is no such parameter.
        var response = await GetAsync("me/pto-balance?employeeId=demo-hr&oid=demo-hr", token);

        Assert.Equal(86.5, (await response.Content.ReadFromJsonAsync<PtoBalance>(Ct))!.AvailableHours);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("employees/demo-hr/pto-balance", token)).StatusCode);
    }

    [Fact]
    public async Task AnAppOnlyTokenIsRefused()
    {
        // What PriorAuthBot would get from the client credentials flow: its own identity, an
        // application role, no user, no delegated scope. The HR API must not serve it.
        var appOnly = Sign(new Claim("azp", "priorauthbot-development"), new Claim("roles", "Pto.Read.All"));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("me/pto-balance", appOnly)).StatusCode);
    }

    [Fact]
    public async Task AUserTokenWithoutTheScopeIsRefused()
    {
        var noScope = Sign(new Claim("oid", "demo-nurse"), new Claim("scp", "Profile.Read"));

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("me/pto-balance", noScope)).StatusCode);
    }

    [Fact]
    public async Task NoTokenIsUnauthorized() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await hr.CreateClient().GetAsync("me/pto-balance", Ct)).StatusCode);

    [Fact]
    public async Task TheDevelopmentTokenNamesTheUserTheScopeAndPriorAuthBot()
    {
        var token = await MockHrFactory.Tokens().GetTokenAsync(new ToolCaller(ToolTestHost.Claims), PtoRead, Ct);

        var jwt = new JsonWebToken(token);
        Assert.Equal("demo-claims", jwt.GetClaim("oid").Value);
        Assert.Equal("Pto.Read", jwt.GetClaim("scp").Value);
        Assert.Equal("priorauthbot-development", jwt.GetClaim("azp").Value);
        Assert.Equal(["api://dd-hr"], jwt.Audiences);
        Assert.True(jwt.ValidTo <= DateTime.UtcNow.AddMinutes(5).AddSeconds(5));
    }

    [Fact]
    public async Task OnBehalfOfNeverFallsBackToTheAppsOwnIdentity()
    {
        var provider = new OnBehalfOfTokenProvider(Options.Create(new DownstreamApiOptions { TenantId = "t", ClientId = "c", ManagedIdentityClientId = "m" }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.GetTokenAsync(new ToolCaller(ToolTestHost.Nurse, AccessToken: null), PtoRead, Ct));
        Assert.Contains("no user access token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolCallerNeverPrintsItsToken() =>
        Assert.DoesNotContain("secret-token", new ToolCaller(ToolTestHost.Nurse, "secret-token").ToString(), StringComparison.Ordinal);

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await hr.CreateClient().SendAsync(request, Ct);
    }

    private static string Sign(params Claim[] claims) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "priorauthbot-development",
        Audience = "api://dd-hr",
        Expires = DateTime.UtcNow.AddMinutes(5),
        Subject = new ClaimsIdentity(claims),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(MockHrFactory.SigningKey)), SecurityAlgorithms.HmacSha256),
    });
}
