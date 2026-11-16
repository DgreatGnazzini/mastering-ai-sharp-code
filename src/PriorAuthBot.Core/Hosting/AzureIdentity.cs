using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PriorAuthBot.Core.Hosting;

/// <summary>
/// Chapter 14: how PriorAuthBot proves who it is to Azure. One credential, registered once, used by
/// every Azure client: Azure OpenAI, PostgreSQL, AI Search, Content Safety, Document Intelligence,
/// Blob Storage, and Azure Monitor. None of them gets a key.
/// </summary>
public sealed class AzureIdentityOptions
{
    public const string SectionName = "Azure";

    /// <summary>
    /// The client ID of the user-assigned managed identity the container app runs as. Set by the
    /// Bicep in <c>infra/</c>. When it is set, PriorAuthBot uses that identity and nothing else; when
    /// it is not (a developer's machine), <see cref="DefaultAzureCredential"/> finds your Azure CLI or
    /// Visual Studio sign-in.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>The Entra ID tenant, for the on-behalf-of flow and for <c>az login</c> on a machine with several tenants.</summary>
    public string? TenantId { get; set; }
}

public static class AzureIdentity
{
    /// <summary>Entra ID's scope for Azure Database for PostgreSQL. The token is the password.</summary>
    public const string PostgresScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Entra ID's scope for Azure OpenAI and the other Azure AI services.</summary>
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// The credential for this configuration. In Azure it is exactly one managed identity. A
    /// <see cref="DefaultAzureCredential"/> in production tries a chain of sources (environment
    /// variables, workload identity, managed identity, developer tools) and uses the first that
    /// answers, which makes "which identity did this call use?" a question with more than one answer.
    /// </summary>
    public static TokenCredential CreateCredential(AzureIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            return new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
        }

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(options.TenantId) ? null : options.TenantId,

            // On a developer's machine there is no managed identity to find, and probing for one
            // costs seconds on every cold start.
            ExcludeManagedIdentityCredential = true,
            ExcludeWorkloadIdentityCredential = true,
        });
    }

    public static AzureIdentityOptions Options(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(AzureIdentityOptions.SectionName).Get<AzureIdentityOptions>() ?? new AzureIdentityOptions();
    }

    /// <summary>The registered credential, or a developer's default when nothing registered one (unit tests).</summary>
    public static TokenCredential AzureCredential(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<TokenCredential>() ?? CreateCredential(new AzureIdentityOptions());
    }

    /// <summary>Registers the one <see cref="TokenCredential"/> every Azure client in PriorAuthBot uses.</summary>
    public static IServiceCollection AddPriorAuthBotAzureIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AzureIdentityOptions>().Bind(configuration.GetSection(AzureIdentityOptions.SectionName));
        services.TryAddSingleton(_ => CreateCredential(Options(configuration)));
        return services;
    }
}
