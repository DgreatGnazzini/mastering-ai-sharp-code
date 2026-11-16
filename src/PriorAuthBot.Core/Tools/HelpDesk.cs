using System.Collections.Concurrent;
using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Tools;

/// <summary>What the user asked the help desk for. Every field is bounded and typed.</summary>
public sealed record TicketRequest(string Summary, TicketCategory Category, TicketUrgency Urgency);

public enum TicketCategory
{
    Hardware,
    Software,
    Access,
    Network,
    Other,
}

public enum TicketUrgency
{
    Low,
    Normal,
    High,
}

public sealed record HelpDeskTicket(string Number, string RequesterId, string RequesterName, TicketRequest Request, DateTimeOffset Opened);

/// <summary>
/// The IT help desk (chapter 10). The book's version keeps tickets in memory; a real one calls the
/// help desk's API on behalf of the user, as <see cref="HrClient"/> does.
/// </summary>
public interface IHelpDesk
{
    /// <summary>
    /// Opens a ticket for <paramref name="requester"/>. The same <paramref name="idempotencyKey"/>
    /// always returns the same ticket, so a retried or double-clicked approval opens one ticket, not two.
    /// </summary>
    Task<HelpDeskTicket> OpenTicketAsync(TicketRequest request, CallerIdentity requester, string idempotencyKey, CancellationToken cancellationToken = default);

    IReadOnlyList<HelpDeskTicket> TicketsFor(string requesterId);
}

public sealed class InMemoryHelpDesk(TimeProvider? time = null) : IHelpDesk
{
    private readonly ConcurrentDictionary<string, HelpDeskTicket> _byKey = new(StringComparer.Ordinal);
    private int _next = 10_000;

    public int Count => _byKey.Count;

    public Task<HelpDeskTicket> OpenTicketAsync(TicketRequest request, CallerIdentity requester, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var ticket = _byKey.GetOrAdd(idempotencyKey, _ => new HelpDeskTicket(
            $"INC-{Interlocked.Increment(ref _next)}", requester.UserId, requester.DisplayName, request, (time ?? TimeProvider.System).GetUtcNow()));
        return Task.FromResult(ticket);
    }

    public IReadOnlyList<HelpDeskTicket> TicketsFor(string requesterId) =>
        [.. _byKey.Values.Where(t => t.RequesterId == requesterId).OrderBy(t => t.Number, StringComparer.Ordinal)];
}
