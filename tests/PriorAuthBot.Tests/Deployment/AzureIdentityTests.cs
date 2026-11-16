using Azure.Core;
using Azure.Identity;
using Npgsql;
using PriorAuthBot.Core.Data;
using PriorAuthBot.Core.Hosting;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>Chapter 14: one managed identity in Azure, and PostgreSQL signed in with its token.</summary>
public sealed class AzureIdentityTests
{
    [Fact]
    public void InAzureTheCredentialIsExactlyTheAppsManagedIdentity()
    {
        var credential = AzureIdentity.CreateCredential(new AzureIdentityOptions { ManagedIdentityClientId = "11111111-1111-1111-1111-111111111111" });

        Assert.IsType<ManagedIdentityCredential>(credential);
    }

    [Fact]
    public void OnALaptopItIsTheDeveloperChain()
    {
        Assert.IsType<DefaultAzureCredential>(AzureIdentity.CreateCredential(new AzureIdentityOptions()));
    }

    [Fact]
    public void EntraSignInRefusesAConnectionStringWithAPassword()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PostgresDataSource.Create(
            "Host=psql;Database=priorauthbot;Username=id-priorauthbot-api;Password=hunter2", new PostgresOptions { Authentication = PostgresOptions.Entra }, () => new CountingCredential()));

        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntraSignInSendsAnAccessTokenForTheDatabaseScopeAsThePassword()
    {
        var credential = new CountingCredential();
        await using var dataSource = PostgresDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=priorauthbot;Username=id-priorauthbot-api;Timeout=2", new PostgresOptions { Authentication = PostgresOptions.Entra }, () => credential);

        // Nothing listens on port 1, so the connection fails, but only after the token was fetched.
        await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken));

        Assert.Contains(AzureIdentity.PostgresScope, credential.Scopes);
        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).SslMode);
    }

    [Fact]
    public void AnUnknownSignInIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => PostgresDataSource.Create("Host=psql", new PostgresOptions { Authentication = "Kerberos" }));
    }

    private sealed class CountingCredential : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
