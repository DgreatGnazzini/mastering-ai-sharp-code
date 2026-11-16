using System.Net;
using System.Text;
using System.Text.Json;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Eval;

/// <summary>
/// The HR API as the eval sees it (chapter 10): the mock HR system's own data, served without a
/// network. The token names the user, as an on-behalf-of token does, and the handler answers only
/// for that user. The eval measures which tool the model picks, not the token exchange, which
/// the tests cover against the real mock API.
/// </summary>
internal sealed class EvalHrHandler : HttpMessageHandler
{
    private readonly Dictionary<string, JsonElement> _employees =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(EvalPaths.MockHrEmployees)) ?? [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = request.Headers.Authorization?.Parameter?.Replace("eval-token-for:golden-", "demo-", StringComparison.Ordinal);
        if (user is null || !_employees.TryGetValue(user, out var employee))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var body = JsonSerializer.Serialize(new
        {
            employeeName = employee.GetProperty("name").GetString(),
            availableHours = employee.GetProperty("availableHours").GetDouble(),
            accruedThisYearHours = employee.GetProperty("accruedThisYearHours").GetDouble(),
            usedThisYearHours = employee.GetProperty("usedThisYearHours").GetDouble(),
            carryoverLimitHours = employee.GetProperty("carryoverLimitHours").GetDouble(),
            asOf = "2026-11-14",
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

internal sealed class EvalTokenProvider : IDownstreamTokenProvider
{
    public ValueTask<string> GetTokenAsync(ToolCaller caller, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult("eval-token-for:" + caller.UserId);
}
