using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Tools;

/// <summary>
/// One tool, with what PriorAuthBot needs to know about it besides its schema: whether it changes
/// anything. A write tool reaches the model wrapped in <see cref="ApprovalRequiredAIFunction"/>, so
/// <see cref="FunctionInvokingChatClient"/> returns an approval request instead of calling it.
/// </summary>
public sealed record ToolEntry(AIFunction Function, bool IsWrite, string Source = "priorauthbot")
{
    public string Name => Function.Name;

    /// <summary>What the model is given: the function itself, or for a write, an approval request in its place.</summary>
    public AITool ForModel => IsWrite ? new ApprovalRequiredAIFunction(Function) : Function;
}

/// <summary>
/// PriorAuthBot's own tools (chapter 10). They are built per request for one <see cref="ToolCaller"/>,
/// and each one closes over that caller. No tool takes a user, an employee ID, or a group as an
/// argument, so nothing the model writes can make a tool act for someone else.
/// </summary>
public sealed partial class PriorAuthBotTools(SecureRetriever retriever, HrClient hr, IHelpDesk helpDesk)
{
    public const string LookupPolicy = "lookup_policy";
    public const string GetPtoBalance = "get_pto_balance";
    public const string OpenHelpDeskTicket = "open_help_desk_ticket";

    /// <summary>
    /// Set in <see cref="AIFunctionArguments.Context"/> only by <c>AssistantService.ApproveAsync</c>,
    /// after the user approved this exact call. A write tool refuses to run without it.
    /// </summary>
    public const string ApprovedActionKey = "priorauthbot.approved-action";

    private static readonly System.Text.Json.JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    public IReadOnlyList<ToolEntry> For(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return
        [
            new(AIFunctionFactory.Create(
                ([Description("The policy number, such as HR-104 or CLN-410.")] string policyNumber, CancellationToken cancellationToken) =>
                    LookupPolicyAsync(policyNumber, caller.Identity, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = LookupPolicy,
                    Description = "Looks up one Deductible Dynamics policy by its policy number and returns its title, its sections, and a link. Returns found=false if there is no such policy the user can read.",
                }), IsWrite: false),

            new(AIFunctionFactory.Create(
                (CancellationToken cancellationToken) => GetPtoBalanceAsync(caller, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = GetPtoBalance,
                    Description = "Returns the signed-in user's own paid time off balance in hours, from the HR system. It takes no arguments and cannot look up anyone else.",
                }), IsWrite: false),

            new(AIFunctionFactory.Create(
                ([Description("One or two sentences describing the problem, in the user's words. At most 300 characters.")] string summary,
                 [Description("What kind of problem it is.")] TicketCategory category,
                 [Description("High only if the user cannot work at all.")] TicketUrgency urgency,
                 AIFunctionArguments arguments,
                 CancellationToken cancellationToken) =>
                    OpenTicketAsync(new TicketRequest(summary, category, urgency), caller, arguments, cancellationToken),
                new AIFunctionFactoryOptions
                {
                    Name = OpenHelpDeskTicket,
                    Description = "Opens an IT help desk ticket for the signed-in user. The user must approve the ticket before it is opened.",
                }), IsWrite: true),
        ];
    }

    /// <summary>A short, human description of a write, for the approval card.</summary>
    public static string Describe(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string Arg(string name) => arguments.TryGetValue(name, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
        return toolName == OpenHelpDeskTicket
            ? $"Open a help desk ticket ({Arg("category")}, {Arg("urgency")} urgency): \"{Arg("summary")}\""
            : $"Run {toolName} with {System.Text.Json.JsonSerializer.Serialize(arguments, Compact)}";
    }

    private async Task<PolicyLookupResult> LookupPolicyAsync(string policyNumber, CallerIdentity caller, CancellationToken cancellationToken)
    {
        // Validate the model's argument like any other untrusted input before it reaches a query.
        var normalized = policyNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!PolicyNumberPattern().IsMatch(normalized))
        {
            return PolicyLookupResult.NotFound(normalized, "That is not a policy number. Policy numbers look like HR-104.");
        }

        var document = await retriever.GetDocumentByPolicyNumberAsync(normalized, caller, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            // The same answer whether the policy is missing or restricted (chapter 8's rule).
            return PolicyLookupResult.NotFound(normalized, null);
        }

        return new PolicyLookupResult(
            Found: true,
            PolicyNumber: normalized,
            Title: document.Title,
            Link: $"/documents/{document.DocumentId}",
            Sections: [.. document.Sections.Select(s => new PolicySection(s.Section, Excerpt(s.Text)))],
            Note: null);
    }

    private async Task<PtoBalanceResult> GetPtoBalanceAsync(ToolCaller caller, CancellationToken cancellationToken)
    {
        var balance = await hr.GetMyPtoBalanceAsync(caller, cancellationToken).ConfigureAwait(false);
        return balance is null
            ? new PtoBalanceResult(false, null, "The HR system has no PTO record for this user. Contractors and temporary staff do not accrue PTO.")
            : new PtoBalanceResult(true, balance, null);
    }

    private async Task<TicketResult> OpenTicketAsync(TicketRequest request, ToolCaller caller, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // Defense in depth: FunctionInvokingChatClient never calls an approval-required function
        // here, because PriorAuthBot never hands it an approval. If anything else ever calls this
        // tool without an approved action, it refuses.
        if (arguments.Context?.TryGetValue(ApprovedActionKey, out var approved) != true || approved is not string actionId)
        {
            throw new InvalidOperationException("open_help_desk_ticket runs only after the user approves it.");
        }

        var summary = request.Summary.Trim();
        if (summary.Length is 0 or > 300)
        {
            throw new ArgumentException("A ticket summary must be 1 to 300 characters.", nameof(request));
        }

        var ticket = await helpDesk.OpenTicketAsync(request with { Summary = summary }, caller.Identity, actionId, cancellationToken).ConfigureAwait(false);
        return new TicketResult(ticket.Number, ticket.Request.Summary, ticket.Request.Category, ticket.Request.Urgency);
    }

    private static string Excerpt(string text) => text.Length <= 300 ? text : text[..300] + "...";

    [GeneratedRegex(@"^[A-Z]{2,4}-\d{3}(-\d{3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyNumberPattern();
}

public sealed record PolicySection(string Heading, string Excerpt);

public sealed record PolicyLookupResult(bool Found, string PolicyNumber, string? Title, string? Link, IReadOnlyList<PolicySection> Sections, string? Note)
{
    public static PolicyLookupResult NotFound(string policyNumber, string? note) => new(false, policyNumber, null, null, [], note ?? "No policy with that number is available to this user.");
}

public sealed record PtoBalanceResult(bool HasPtoRecord, PtoBalance? Balance, string? Note);

public sealed record TicketResult(string TicketNumber, string Summary, TicketCategory Category, TicketUrgency Urgency);
