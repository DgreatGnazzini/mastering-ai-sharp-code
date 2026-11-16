using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// The chat client used when <c>AI:Provider</c> is <c>Offline</c>. It never calls a model: it
/// echoes the first source it was given, so the API runs end to end in tests and CI. When it is
/// offered tools (chapter 10), it picks one with a few keyword rules, so the tool loop, approvals,
/// and the tool eval also run with no model. The rules are a test double, not a router to copy.
/// </summary>
public sealed partial class OfflineChatClient : IChatClient
{
    private static readonly System.Text.Json.JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    // Structured output parses strictly (chapter 3): a null field must be present, not left out.
    private static readonly System.Text.Json.JsonSerializerOptions WithNulls = new(System.Text.Json.JsonSerializerDefaults.Web);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        if (options?.Tools is { Count: > 0 } tools)
        {
            return Task.FromResult(WithTools(list, tools));
        }

        // Chapter 11: the exception workflow's first step asks for this schema. A few rules stand in
        // for the model, so the workflow, its checks, and the agent eval run with no model.
        if (options?.ResponseFormat is ChatResponseFormatJson { SchemaName: "ExceptionRequestFacts" })
        {
            var request = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ExceptionFacts(request))));
        }

        var context = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var start = context.IndexOf("[1]", StringComparison.Ordinal);
        var text = start < 0
            ? "Offline mode has no model to answer with."
            : "Offline mode (no model). The most relevant source says: " + context[start..].Split('\n', 3).ElementAtOrDefault(1)?.Trim() + " [1]";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    private static ChatResponse WithTools(IList<ChatMessage> messages, IList<AITool> tools)
    {
        // After a tool ran: report its result.
        if (messages.Count > 0 && messages[^1].Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(result.Result, Compact);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Offline mode (no model). The tool returned: {(json.Length > 600 ? json[..600] + "..." : json)}"));
        }

        var question = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var names = tools.Select(t => t.Name).ToList();
        var call = PickTool(question, names);
        return call is null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "Offline mode (no model). I can look up a policy by number, check your PTO balance, or open a help desk ticket."))
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"offline-{Guid.NewGuid():N}", call.Value.Name, call.Value.Arguments)]));
    }

    /// <summary>The offline client's tool choice for a question, or null for "no tool".</summary>
    public static (string Name, Dictionary<string, object?> Arguments)? PickTool(string question, IReadOnlyCollection<string> tools)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(tools);
        var q = question.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => q.Contains(w, StringComparison.Ordinal));

        if ((Has("tiers") || (Has("each") && Has("tier"))) && tools.FirstOrDefault(t => t.EndsWith("_list_tiers", StringComparison.Ordinal)) is { } tiers)
        {
            return (tiers, []);
        }

        if (Has("formulary", "covered", "tier") && tools.FirstOrDefault(t => t.EndsWith("_search_formulary", StringComparison.Ordinal)) is { } formulary)
        {
            return (formulary, new() { ["query"] = question });
        }

        if (PolicyNumber().Match(question) is { Success: true } match && tools.Contains("lookup_policy"))
        {
            return ("lookup_policy", new() { ["policyNumber"] = match.Value.ToUpperInvariant() });
        }

        if (Has("pto", "vacation", "time off") && Has("balance", "left", "how much", "how many", "do i have") && tools.Contains("get_pto_balance"))
        {
            return ("get_pto_balance", []);
        }

        if (Has("ticket", "help desk", "broken", "not working", "won't", "locked out", "reset") && tools.Contains("open_help_desk_ticket"))
        {
            var category = Has("laptop", "monitor", "screen", "keyboard", "mouse", "printer", "headset") ? "Hardware"
                : Has("password", "locked out", "access", "permission") ? "Access"
                : Has("vpn", "wifi", "wi-fi", "network", "internet") ? "Network"
                : Has("install", "outlook", "excel", "teams", "software", "app") ? "Software"
                : "Other";
            var urgency = Has("urgent", "asap", "can't work", "cannot work") ? "High" : "Normal";
            return ("open_help_desk_ticket", new() { ["summary"] = question.Length <= 300 ? question : question[..300], ["category"] = category, ["urgency"] = urgency });
        }

        return null;
    }

    /// <summary>The offline client's reading of an exception request, as JSON for <c>ExceptionRequestFacts</c>.</summary>
    public static string ExceptionFacts(string request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = PolicyNumber().Match(request) is { Success: true } p ? p.Value.ToUpperInvariant() : null;
        double? hours = null;
        if (Hours().Match(request) is { Success: true } h)
        {
            var value = double.Parse(h.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            hours = h.Groups["unit"].Value.StartsWith('d') || h.Groups["unit"].Value.StartsWith('D') ? value * 8 : value;
        }

        var because = request.IndexOf("because", StringComparison.OrdinalIgnoreCase);
        var reason = (because >= 0 ? request[(because + "because".Length)..] : request).Trim(' ', '.', ',');
        var sentence = request.Split('.', 2)[0].Trim();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            policyNumber = policy,
            request = sentence.Length is >= 3 and <= 200 ? sentence : "A policy exception",
            requestedHours = hours,
            reason = reason.Length is >= 3 and <= 400 ? reason : (reason.Length > 400 ? reason[..400] : "Not given"),
        }, WithNulls);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b[A-Za-z]{2,4}-\d{3}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PolicyNumber();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<n>\d+(\.\d+)?)\s*(?<unit>hours|hrs|hour|h|days|day)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Hours();

    /// <summary>Chapter 15: the offline client names itself, like any model, for caches and the audit log.</summary>
    private static readonly ChatClientMetadata Metadata = new("offline", null, "offline");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? Metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }
}
