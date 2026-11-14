using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Core.Tools;

/// <summary>A person's PTO balance, as the HR system reports it.</summary>
public sealed record PtoBalance(string EmployeeName, double AvailableHours, double AccruedThisYearHours, double UsedThisYearHours, double CarryoverLimitHours, DateOnly AsOf);

/// <summary>
/// The HR system's API (a mock in this book, <c>samples/mock-apis/DeductibleDynamics.MockHr</c>).
/// There is one call, "my balance". It takes no employee ID: the HR API reads the user from the token.
/// </summary>
public sealed class HrClient(HttpClient http, IDownstreamTokenProvider tokens, IOptions<HrApiOptions> options)
{
    public const string HttpClientName = "hr";

    public async Task<PtoBalance?> GetMyPtoBalanceAsync(ToolCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var token = await tokens.GetTokenAsync(caller, options.Value.Scopes, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "me/pto-balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // 404: the HR system has no PTO record for this person, such as a contractor.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PtoBalance>(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class HrApiOptions
{
    public const string SectionName = "Tools:Hr";

    public Uri BaseAddress { get; set; } = new("http://localhost:5180/");

    /// <summary>The delegated scope PriorAuthBot asks for. Read only: PriorAuthBot cannot change a balance.</summary>
    public string[] Scopes { get; set; } = ["api://dd-hr/Pto.Read"];
}
