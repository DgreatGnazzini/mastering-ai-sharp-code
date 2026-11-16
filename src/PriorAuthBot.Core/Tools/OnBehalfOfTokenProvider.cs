using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Core.Tools;

/// <summary>
/// The OAuth 2.0 on-behalf-of flow with Entra ID (chapter 10). PriorAuthBot sends Entra the user's
/// token for PriorAuthBot and gets back a token for the HR API that names the same user, with
/// PriorAuthBot as the client. The HR API authorizes the user; PriorAuthBot gains no rights of its own.
/// </summary>
/// <remarks>
/// PriorAuthBot proves its own identity to Entra with a client assertion from its managed identity
/// (a federated identity credential, set up in chapter 14), so there is no client secret to store.
/// </remarks>
public sealed class OnBehalfOfTokenProvider(IOptions<DownstreamApiOptions> options) : IDownstreamTokenProvider
{
    public async ValueTask<string> GetTokenAsync(ToolCaller caller, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(scopes);

        // No user token means no user to act for. Never fall back to the app's own identity.
        var assertion = caller.AccessToken ?? throw new InvalidOperationException(
            "The request has no user access token, so there is no user to call the downstream API on behalf of.");

        var settings = options.Value;
        var managedIdentity = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
            settings.ManagedIdentityClientId ?? throw new InvalidOperationException("Tools:Downstream:ManagedIdentityClientId is not set.")));
        var credential = new OnBehalfOfCredential(
            settings.TenantId ?? throw new InvalidOperationException("Tools:Downstream:TenantId is not set."),
            settings.ClientId ?? throw new InvalidOperationException("Tools:Downstream:ClientId is not set."),
            async ct => (await managedIdentity.GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange/.default"]), ct).ConfigureAwait(false)).Token,
            assertion,
            new OnBehalfOfCredentialOptions());

        var token = await credential.GetTokenAsync(new TokenRequestContext([.. scopes]), cancellationToken).ConfigureAwait(false);
        return token.Token;
    }
}

/// <summary>PriorAuthBot's app registration, for the on-behalf-of flow.</summary>
public sealed class DownstreamApiOptions
{
    public const string SectionName = "Tools:Downstream";

    public string? TenantId { get; set; }

    /// <summary>PriorAuthBot's own app registration (the audience of the user's token).</summary>
    public string? ClientId { get; set; }

    /// <summary>The user-assigned managed identity trusted by the app registration's federated credential.</summary>
    public string? ManagedIdentityClientId { get; set; }
}
