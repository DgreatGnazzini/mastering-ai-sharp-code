using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Data;
using PriorAuthBot.Core.Usage;

namespace PriorAuthBot.Core.Hosting;

/// <summary>
/// Every setting that is fine on a developer's machine and a defect anywhere else, checked once at
/// startup. Chapters 12 and 13 added the first rules to Program.cs one at a time; chapter 14 collects
/// them here, adds the ones a deployment needs, and reports every problem at once, so a bad
/// configuration fails the new revision's startup (and with it the canary) instead of serving traffic.
/// </summary>
public static partial class ProductionReadiness
{
    /// <summary>Returns every problem with this configuration outside Development. Empty means ready.</summary>
    public static IReadOnlyList<string> Check(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var problems = new List<string>();
        void Require(bool ok, string problem)
        {
            if (!ok)
            {
                problems.Add(problem);
            }
        }

        // Chapter 12.
        Require(configuration.GetValue("Security:Guardrails", true),
            "Security:Guardrails can be false only in the Development environment.");

        // Chapter 13.
        Require(!configuration.GetValue<bool>("Telemetry:CaptureMessageContent"),
            "Telemetry:CaptureMessageContent can be true only in the Development environment: traces would carry prompts and answers, which can hold PHI.");
        Require(!string.Equals(configuration["Logging:LogLevel:Microsoft.Extensions.AI"], "Trace", StringComparison.OrdinalIgnoreCase),
            "Logging for Microsoft.Extensions.AI can be Trace only in the Development environment: at Trace, LoggingChatClient logs every message.");
        Require((configuration["Audit:Store"] ?? "InMemory") != "InMemory",
            "Audit:Store must be a durable, append-only store (Postgres) outside the Development environment.");

        // Chapter 14: sign-in.
        var authMode = configuration["Auth:Mode"] ?? "Entra";
        Require(authMode == "Entra", $"Auth:Mode must be Entra outside the Development environment, not '{authMode}'.");
        Require(!string.IsNullOrWhiteSpace(configuration["Auth:JwtBearer:Audience"]), "Auth:JwtBearer:Audience is not set.");
        Require(!string.IsNullOrWhiteSpace(configuration["Auth:RequiredRole"]),
            "Auth:RequiredRole is not set: every signed-in employee of the tenant could use PriorAuthBot, not only the people assigned to it.");

        // Chapter 14: one ledger for all replicas.
        var usage = configuration.GetSection(UsageOptions.SectionName).Get<UsageOptions>() ?? new UsageOptions();
        Require(!usage.QuotasEnabled || usage.Ledger == UsageOptions.PostgresLedger,
            "Usage:Ledger must be Postgres outside the Development environment: an in-memory ledger gives every user one budget per replica.");

        // Chapter 14: Azure clients sign in as the app's managed identity, and only as it.
        var usesAzure = new List<string>();
        if (configuration["AI:Provider"] == "AzureOpenAI") usesAzure.Add("AI:Provider");
        if (configuration["VectorStore:Provider"] == "AzureAISearch") usesAzure.Add("VectorStore:Provider");
        if (configuration[$"{PostgresOptions.SectionName}:Authentication"] == PostgresOptions.Entra) usesAzure.Add("Postgres:Authentication");
        if (configuration["Audit:Archive:Provider"] == AuditArchiveOptions.Blob) usesAzure.Add("Audit:Archive:Provider");
        if (configuration["Security:PromptShield:Provider"] == "ContentSafety") usesAzure.Add("Security:PromptShield:Provider");
        Require(usesAzure.Count == 0 || !string.IsNullOrWhiteSpace(configuration["Azure:ManagedIdentityClientId"]),
            $"Azure:ManagedIdentityClientId is not set, and {string.Join(", ", usesAzure)} use Azure: outside Development, Azure clients sign in as the app's managed identity, never with a developer's credential chain.");

        // Chapter 14: nothing left over from the template, and no secrets in files.
        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (value is not null && Placeholder().IsMatch(value))
            {
                problems.Add($"{key} still holds the placeholder '{value}'.");
            }
        }

        if (configuration is IConfigurationRoot root)
        {
            foreach (var (key, provider) in SecretsInFiles(root))
            {
                problems.Add($"{key} holds a secret and comes from {provider}. Outside Development, secrets come from the environment (a Container Apps secret backed by Key Vault), never from a file in the image; better still, use a managed identity and have no secret at all.");
            }
        }

        return problems;
    }

    /// <summary>Throws with every problem when the configuration is not ready for this environment.</summary>
    public static void EnsureReady(IConfiguration configuration, string environmentName)
    {
        var problems = Check(configuration, environmentName);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"PriorAuthBot's configuration is not ready for the {environmentName} environment:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", problems)}");
        }
    }

    /// <summary>
    /// Keys whose effective value looks like a secret and is supplied by a file (appsettings*.json).
    /// The last provider that has a key wins, so a file value overridden by an environment variable is fine.
    /// </summary>
    internal static IEnumerable<(string Key, string Provider)> SecretsInFiles(IConfigurationRoot root)
    {
        foreach (var (key, value) in root.AsEnumerable())
        {
            if (string.IsNullOrEmpty(value) || !LooksSecret(key, value))
            {
                continue;
            }

            var winner = root.Providers.Reverse().FirstOrDefault(p => p.TryGet(key, out _));
            if (winner is FileConfigurationProvider file)
            {
                yield return (key, file.Source.Path ?? "a configuration file");
            }
        }
    }

    internal static bool LooksSecret(string key, string value)
    {
        var name = key[(key.LastIndexOf(':') + 1)..];
        return SecretName().IsMatch(name)
            || value.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^<[^<>]+>$|<[a-z0-9-]+-id>", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex("(ApiKey|Password|Secret|SigningKey|AccessKey)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}
