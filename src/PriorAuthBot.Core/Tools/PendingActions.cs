using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PriorAuthBot.Core.Tools;

/// <summary>
/// A write the model proposed and the user has not approved yet (chapter 10). It holds the exact
/// arguments the model proposed; approval runs those, not new ones.
/// </summary>
public sealed record PendingAction(
    string Id,
    string OwnerId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    string Description,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Proposed writes waiting for the user. An action can be approved once, only by the user it was
/// proposed for, and only before it expires. This store is in memory, so one API instance owns
/// each action; with more instances, back it with a shared store (chapter 14).
/// </summary>
public sealed class PendingActionStore(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PendingAction> _actions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public PendingAction Add(string ownerId, string toolName, IReadOnlyDictionary<string, object?> arguments, string description)
    {
        // 128 random bits: an ID cannot be guessed, and it is still checked against the owner.
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var action = new PendingAction(id, ownerId, toolName, arguments, description, _time.GetUtcNow() + Lifetime);
        _actions[id] = action;
        return action;
    }

    /// <summary>
    /// Removes and returns the action if <paramref name="ownerId"/> owns it and it has not expired.
    /// Returns null otherwise, the same for "no such action" and "someone else's action".
    /// </summary>
    public PendingAction? Take(string id, string ownerId)
    {
        if (!_actions.TryGetValue(id, out var action) || action.OwnerId != ownerId)
        {
            return null;
        }

        // TryRemove with the value makes taking atomic: two concurrent approvals cannot both win.
        if (!_actions.TryRemove(new KeyValuePair<string, PendingAction>(id, action)))
        {
            return null;
        }

        return action.ExpiresAt > _time.GetUtcNow() ? action : null;
    }

    public IReadOnlyList<PendingAction> For(string ownerId) =>
        [.. _actions.Values.Where(a => a.OwnerId == ownerId && a.ExpiresAt > _time.GetUtcNow()).OrderBy(a => a.ExpiresAt)];
}
