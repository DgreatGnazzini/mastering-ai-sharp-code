using System.ComponentModel;
using ModelContextProtocol.Server;
using PriorAuthBot.Core.Identity;
using PriorAuthBot.Core.Rag;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Api.Mcp;

public sealed record PolicySearchResult(string DocumentId, string Title, string? PolicyNumber, string Section, string Text, string Link);

/// <summary>
/// PriorAuthBot's permission-aware search, published as an MCP tool (chapter 10). Any MCP client,
/// such as an IDE or a desktop assistant, can call it, and it gets exactly what the signed-in user
/// could get from <c>POST /search</c>: the request is authenticated by the same JWT handler, the
/// caller's groups come from the same token, and the search runs through the same SecureRetriever.
/// </summary>
[McpServerToolType]
public sealed class PolicySearchMcpTools(IHttpContextAccessor http, CallerIdentityFactory identities, SecureRetriever retriever)
{
    [McpServerTool(Name = "search_policies", Title = "Search Deductible Dynamics policies", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the Deductible Dynamics policies the signed-in user may read, and returns the most relevant passages with links.")]
    public async Task<IReadOnlyList<PolicySearchResult>> SearchPoliciesAsync(
        [Description("What to search for, in plain words or a policy number.")] string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500)
        {
            throw new ArgumentException("Send a query of 1 to 500 characters.", nameof(query));
        }

        // The user from this HTTP request, validated by the JWT handler before the MCP endpoint ran.
        var user = http.HttpContext?.User ?? throw new InvalidOperationException("No HTTP request.");
        var results = await retriever.SearchAsync(query, identities.Create(user), cancellationToken);
        return [.. results.Select(r => new PolicySearchResult(r.Record.DocumentId, r.Record.Title, r.Record.PolicyNumber, r.Record.Section, r.Record.Text, Citations.Link(r.Record)))];
    }
}
