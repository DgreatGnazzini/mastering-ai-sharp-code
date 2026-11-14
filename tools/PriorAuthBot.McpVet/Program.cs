using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using PriorAuthBot.Core.Mcp;

// Usage: dotnet run --project tools/PriorAuthBot.McpVet -- <command> [arguments...]
// Vetting is a person reading what the model would read. This prints all of it, then the entry to
// paste into Mcp:Servers once that person is satisfied. It never marks a tool read-only: that is a
// judgment about what the tool does, and the person makes it by editing the entry.
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/PriorAuthBot.McpVet -- <command> [arguments...]");
    return 2;
}

await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "vet",
    Command = args[0],
    Arguments = args[1..],
    InheritEnvironmentVariables = false,
}));

var info = client.ServerInfo;
Console.WriteLine($"Server: {info?.Name} {info?.Version}");
var entry = new McpServerEntry { Command = args[0], Arguments = [.. args[1..]], ExpectedServerName = info?.Name, ExpectedServerVersion = info?.Version };
var anyProblems = false;
foreach (var tool in await client.ListToolsAsync())
{
    var protocol = tool.ProtocolTool;
    var fingerprint = McpToolVetting.Fingerprint(protocol);
    var findings = McpToolVetting.Lint(protocol.Description ?? string.Empty).ToList();
    anyProblems |= findings.Count > 0;
    Console.WriteLine();
    Console.WriteLine($"Tool: {protocol.Name}");
    Console.WriteLine($"  Description: {protocol.Description}");
    Console.WriteLine($"  Input schema: {JsonSerializer.Serialize(protocol.InputSchema)}");
    Console.WriteLine($"  Server says read-only: {protocol.Annotations?.ReadOnlyHint?.ToString() ?? "(not stated)"}");
    Console.WriteLine($"  Fingerprint: {fingerprint}");
    Console.WriteLine(findings.Count == 0 ? "  Lint: no findings" : $"  Lint: {string.Join("; ", findings)}");
    entry.Tools[protocol.Name] = new McpToolEntry { Sha256 = fingerprint, ReadOnly = false };
}

Console.WriteLine();
Console.WriteLine("Configuration entry (set ReadOnly to true only for tools you checked cannot change anything):");
Console.WriteLine(JsonNode.Parse(JsonSerializer.Serialize(entry, JsonSerializerOptions.Web))!.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
return anyProblems ? 1 : 0;
