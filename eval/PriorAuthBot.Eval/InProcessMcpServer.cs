using System.IO.Pipelines;
using System.Reflection;
using DeductibleDynamics.Formulary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PriorAuthBot.Eval;

/// <summary>
/// An MCP server in this process, connected to a client over two pipes (chapter 10). The protocol
/// is the same as over stdio; only the transport differs, so tests and the eval run without
/// starting a process. Tests use it to stand up servers with poisoned or changed tools.
/// </summary>
internal sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private Task? _running;

    private InProcessMcpServer()
    {
    }

    /// <summary>The formulary sample server's tools, as the real server registers them.</summary>
    public static IEnumerable<McpServerTool> FormularyTools() =>
        typeof(FormularyTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, target: null));

    public static async Task<(InProcessMcpServer Server, McpClient Client)> StartAsync(
        IEnumerable<McpServerTool> tools, string name = FormularyServer.Name, string version = FormularyServer.Version, CancellationToken cancellationToken = default)
    {
        var host = new InProcessMcpServer();
        var options = new McpServerOptions { ServerInfo = new Implementation { Name = name, Version = version }, ToolCollection = [] };
        foreach (var tool in tools)
        {
            options.ToolCollection.Add(tool);
        }

        var server = McpServer.Create(
            new StreamServerTransport(host._toServer.Reader.AsStream(), host._toClient.Writer.AsStream(), name, NullLoggerFactory.Instance),
            options, NullLoggerFactory.Instance, host._services);
        host._running = server.RunAsync(host._stop.Token);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(host._toServer.Writer.AsStream(), host._toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            cancellationToken: cancellationToken);
        return (host, client);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
        await _services.DisposeAsync();
    }
}
