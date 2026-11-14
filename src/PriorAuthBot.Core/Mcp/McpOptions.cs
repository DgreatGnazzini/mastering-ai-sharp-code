namespace PriorAuthBot.Core.Mcp;

/// <summary>
/// The MCP servers PriorAuthBot may use (chapter 10). This is an allowlist: a server that is not
/// here is never started or contacted, and a tool that is not listed under its server is dropped.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public Dictionary<string, McpServerEntry> Servers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class McpServerEntry
{
    public bool Enabled { get; set; } = true;

    /// <summary><c>Stdio</c> (a local process) or <c>Http</c> (a remote server).</summary>
    public string Transport { get; set; } = "Stdio";

    /// <summary>For stdio: the command, such as <c>dnx</c> with a pinned package version in <see cref="Arguments"/>.</summary>
    public string? Command { get; set; }

    public List<string> Arguments { get; set; } = [];

    /// <summary>For stdio: where the process starts. The API sets it to its content root when it is empty.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>For HTTP: the server's endpoint.</summary>
    public Uri? Url { get; set; }

    /// <summary>
    /// The server name and version it must report when it starts. A different version is a
    /// different server, and it gets no tools until someone vets it and updates this entry.
    /// </summary>
    public string? ExpectedServerName { get; set; }

    public string? ExpectedServerVersion { get; set; }

    /// <summary>The vetted tools, by the server's own tool name.</summary>
    public Dictionary<string, McpToolEntry> Tools { get; set; } = new(StringComparer.Ordinal);
}

public sealed class McpToolEntry
{
    /// <summary>
    /// SHA-256 of the tool's name, description, and input schema, as vetted. If the server
    /// changes any of them, the tool is dropped: the model would otherwise read text nobody reviewed.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// True only if a person checked that the tool cannot change anything. The server's own
    /// readOnlyHint is a claim by the server, so it is not trusted: anything not marked here is a write
    /// and needs the user's approval.
    /// </summary>
    public bool ReadOnly { get; set; }
}
