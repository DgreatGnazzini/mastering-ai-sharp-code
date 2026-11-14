using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Core.Agents;

/// <summary>A stored conversation: the agent session as Agent Framework serializes it, and who it belongs to.</summary>
public sealed record AgentConversation(string Id, string OwnerId, int Turns, JsonElement Session, DateTimeOffset UpdatedAt);

/// <summary>
/// Where conversations with the policy agent live between requests (chapter 11). Agent Framework
/// serializes an <c>AgentSession</c> to JSON, and this stores that JSON in an
/// <see cref="IDistributedCache"/>: memory on a laptop, Redis or SQL Server when there is more than one
/// API instance (chapter 14). The key includes the owner's user ID, so a conversation ID from someone
/// else simply finds nothing, and the entry expires after <see cref="AgentOptions.ConversationLifetime"/>.
/// The framework's own <c>AgentSessionStore</c> is still marked for evaluation only in 1.28 (MAAI001),
/// which is why PriorAuthBot has this small one.
/// </summary>
public sealed class AgentConversationStore(IDistributedCache cache, IOptionsMonitor<AgentOptions> options, TimeProvider? time = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>128 random bits: a conversation ID cannot be guessed, and it is still checked against the owner.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public async Task<AgentConversation?> GetAsync(string ownerId, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return null;
        }

        var bytes = await cache.GetAsync(Key(ownerId, id), cancellationToken).ConfigureAwait(false);
        var conversation = bytes is null ? null : JsonSerializer.Deserialize<AgentConversation>(bytes, Json);

        // The key already holds the owner. Checking again costs nothing and survives a future change to the key.
        return conversation?.OwnerId == ownerId && conversation.Id == id ? conversation : null;
    }

    public Task SaveAsync(AgentConversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return cache.SetAsync(
            Key(conversation.OwnerId, conversation.Id),
            JsonSerializer.SerializeToUtf8Bytes(conversation with { UpdatedAt = (time ?? TimeProvider.System).GetUtcNow() }, Json),
            new DistributedCacheEntryOptions { SlidingExpiration = options.CurrentValue.ConversationLifetime },
            cancellationToken);
    }

    public Task DeleteAsync(string ownerId, string id, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(Key(ownerId, id), cancellationToken);

    private static string Key(string ownerId, string id) => $"priorauthbot:agent-conversation:{ownerId}:{id}";
}
