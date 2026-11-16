using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace PriorAuthBot.Core.Hosting;

/// <summary>
/// Chapter 15: the fully local profile. With <c>PriorAuthBot:Profile</c> set to <c>LocalOnly</c>,
/// PriorAuthBot refuses to start if any setting would send a question, a document, an embedding, a
/// trace, or an audit record to a service outside Deductible Dynamics' own network. It is a check on
/// configuration, which is where this kind of mistake is made: one environment variable left over
/// from the hosted deployment is enough to send every trace to the cloud.
/// </summary>
/// <remarks>
/// Sign-in is the one exception. Entra ID validates tokens with keys it publishes, so the API fetches
/// those keys; no question or document goes with that request. A site with no route to Entra ID at all
/// needs a local identity provider, which is outside this book.
/// </remarks>
public static class LocalOnlyProfile
{
    public const string ProfileKey = "PriorAuthBot:Profile";

    public const string LocalOnly = "LocalOnly";

    /// <summary>Hosts outside the private ranges that are still inside the network, such as an internal DNS zone.</summary>
    public const string AllowedHostsKey = "LocalOnly:AllowedHosts";

    public static bool IsEnabled(IConfiguration configuration) =>
        string.Equals(configuration?[ProfileKey], LocalOnly, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every setting that would leave the network. Empty means the profile holds, or is not on.</summary>
    public static IReadOnlyList<string> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsEnabled(configuration))
        {
            return [];
        }

        var problems = new List<string>();
        var allowed = configuration.GetSection(AllowedHostsKey).Get<string[]>() ?? [];

        var provider = configuration["AI:Provider"] ?? "Ollama";
        if (!AI.AiOptions.IsLocalProvider(provider))
        {
            problems.Add($"AI:Provider is {provider}, a hosted provider. The local profile allows Ollama or OnnxGenAI.");
        }

        if (configuration.GetValue<bool>("AI:Routing:Enabled"))
        {
            problems.Add("AI:Routing:Enabled is true: routing sends questions without PHI to a hosted model. The local profile sends everything local.");
        }

        Forbid("AI:AzureOpenAI:Endpoint", "Azure OpenAI");
        Forbid("AI:OpenAI:ApiKey", "OpenAI");
        Forbid("Telemetry:AzureMonitor:ConnectionString", "Application Insights");
        Forbid("APPLICATIONINSIGHTS_CONNECTION_STRING", "Application Insights");
        Forbid("VectorStore:AzureAISearch:Endpoint", "Azure AI Search");
        Forbid("Security:PromptShield:Endpoint", "Azure AI Content Safety");
        if (configuration["VectorStore:Provider"] == "AzureAISearch")
        {
            problems.Add("VectorStore:Provider is AzureAISearch. Use Postgres (pgvector) or Qdrant on your own network.");
        }

        if (configuration["Security:PromptShield:Provider"] == "ContentSafety")
        {
            problems.Add("Security:PromptShield:Provider is ContentSafety, which sends every question and document to Azure. Use Heuristic.");
        }

        if (configuration["Audit:Archive:Provider"] is { } archive && archive != "None")
        {
            problems.Add($"Audit:Archive:Provider is {archive}. The archive copies audit records, with prompts, to Azure Blob Storage.");
        }

        if (configuration["Documents:Ocr"] == "DocumentIntelligence")
        {
            problems.Add("Documents:Ocr is DocumentIntelligence, which sends scans to Azure. Use Vision (the local vision model).");
        }

        Private("AI:Ollama:Endpoint", configuration["AI:Ollama:Endpoint"] ?? "http://localhost:11434");
        Private("OTEL_EXPORTER_OTLP_ENDPOINT", configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Private("Tools:Hr:BaseAddress", configuration["Tools:Hr:BaseAddress"]);
        if (configuration["VectorStore:Provider"] == "Qdrant")
        {
            PrivateHost("VectorStore:Qdrant:Host", configuration["VectorStore:Qdrant:Host"] ?? "localhost");
        }

        if (configuration.GetConnectionString("priorauthbot") is { Length: > 0 } connection)
        {
            foreach (var host in (new NpgsqlConnectionStringBuilder(connection).Host ?? "localhost").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                PrivateHost("ConnectionStrings:priorauthbot", host.Split(':')[0]);
            }
        }

        foreach (var server in configuration.GetSection("Mcp:Servers").GetChildren())
        {
            if (server.GetValue("Enabled", true) && server["Transport"] == "Http")
            {
                Private($"Mcp:Servers:{server.Key}:Url", server["Url"]);
            }
        }

        return problems;

        void Forbid(string key, string service)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                problems.Add($"{key} is set, which configures {service}. Remove it for the local profile.");
            }
        }

        void Private(string key, string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                problems.Add($"{key} is not an absolute URL: '{url}'.");
                return;
            }

            PrivateHost(key, uri.Host);
        }

        void PrivateHost(string key, string host)
        {
            if (!IsPrivateHost(host, allowed))
            {
                problems.Add($"{key} points at {host}, which is not on a private network. Add it to {AllowedHostsKey} only if it is inside yours.");
            }
        }
    }

    public static void EnsureLocal(IConfiguration configuration)
    {
        var problems = Check(configuration);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{ProfileKey} is {LocalOnly}, and these settings would send data outside the network:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", problems)}");
        }
    }

    /// <summary>
    /// Loopback, the private address ranges, a name with no dots (a container or a host on the local
    /// network), a reserved private DNS suffix, or a host on the allow list.
    /// </summary>
    public static bool IsPrivateHost(string host, IReadOnlyCollection<string>? allowed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        host = host.Trim('[', ']').TrimEnd('.');
        if (allowed?.Contains(host, StringComparer.OrdinalIgnoreCase) == true || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal || (address.IsIPv4MappedToIPv6 && IsPrivateHost(address.MapToIPv4().ToString()));
            }

            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }
}
