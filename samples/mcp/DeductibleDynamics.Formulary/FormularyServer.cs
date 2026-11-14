using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DeductibleDynamics.Formulary;

/// <summary>
/// The formulary MCP server (chapter 10). The entry point is a named class rather than top-level
/// statements, so the tests can reference this project next to the API without two Program classes.
/// </summary>
public static class FormularyServer
{
    public const string Name = "dd-formulary";
    public const string Version = "1.0.0";

    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the protocol channel for a stdio server, so every log line must go to stderr.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddMcpServer(Configure)
            .WithStdioServerTransport()
            .WithTools<FormularyTools>();
        await builder.Build().RunAsync();
    }

    public static void Configure(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInfo = new() { Name = Name, Version = Version };
    }
}
