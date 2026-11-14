using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace PriorAuthBot.Core.Agents.Exceptions;

/// <summary>One step of an exception run, for the trace: which step, who it ran for, and what it cost.</summary>
public sealed record ExceptionRunStep(DateTimeOffset At, string Step, string ActorId, string? Detail);

/// <summary>
/// Everything PriorAuthBot keeps about one exception request outside the workflow: who may answer
/// it, where it waits, and its trace. The workflow's own state lives in its checkpoints.
/// </summary>
public sealed record ExceptionRun
{
    public required string Id { get; init; }

    public required string RequesterId { get; init; }

    public required string RequesterName { get; init; }

    /// <summary>Set by code from the HR system's answer in step 2, never from anything the model wrote.</summary>
    public string? ApproverId { get; init; }

    public string? ApproverName { get; init; }

    public ExceptionStatus Status { get; init; }

    public string? StopCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public ExceptionDraft? Draft { get; init; }

    /// <summary>The request the workflow is waiting on: its port and ID.</summary>
    public string? PendingPort { get; init; }

    public string? PendingRequestId { get; init; }

    public string? CheckpointId { get; init; }

    public string? DecisionId { get; init; }

    public IReadOnlyList<ExceptionRunStep> Steps { get; init; } = [];

    public int ModelCalls { get; init; }

    public decimal Cost { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public bool IsWaiting => Status is ExceptionStatus.WaitingForRequester or ExceptionStatus.WaitingForApprover;
}

/// <summary>
/// The run records, in an <see cref="IDistributedCache"/> like the agent conversations, with a small
/// index per approver for their review list. A run lives until it expires; an expired run that is
/// still waiting is reported as <see cref="ExceptionStatus.Expired"/> and can no longer be answered.
/// </summary>
public sealed class ExceptionRunStore(IDistributedCache cache, IOptionsMonitor<AgentOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public async Task<ExceptionRun?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return null;
        }

        var bytes = await cache.GetAsync(Key(id), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : JsonSerializer.Deserialize<ExceptionRun>(bytes, Json);
    }

    public async Task SaveAsync(ExceptionRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await cache.SetAsync(Key(run.Id), JsonSerializer.SerializeToUtf8Bytes(run, Json), Expiry(), cancellationToken).ConfigureAwait(false);
        if (run.ApproverId is { } approver)
        {
            var inbox = await InboxAsync(approver, cancellationToken).ConfigureAwait(false);
            if (!inbox.Contains(run.Id))
            {
                inbox.Add(run.Id);
                await cache.SetAsync(InboxKey(approver), JsonSerializer.SerializeToUtf8Bytes(inbox, Json), Expiry(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The runs that name <paramref name="approverId"/> as their approver.</summary>
    public async Task<IReadOnlyList<ExceptionRun>> ForApproverAsync(string approverId, CancellationToken cancellationToken = default)
    {
        var runs = new List<ExceptionRun>();
        foreach (var id in await InboxAsync(approverId, cancellationToken).ConfigureAwait(false))
        {
            if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is { } run && run.ApproverId == approverId)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    private async Task<List<string>> InboxAsync(string approverId, CancellationToken cancellationToken)
    {
        var bytes = await cache.GetAsync(InboxKey(approverId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? [] : JsonSerializer.Deserialize<List<string>>(bytes, Json) ?? [];
    }

    private DistributedCacheEntryOptions Expiry() => new() { AbsoluteExpirationRelativeToNow = options.CurrentValue.ExceptionRequestLifetime + TimeSpan.FromDays(1) };

    private static string Key(string id) => $"priorauthbot:exception-run:{id}";

    private static string InboxKey(string approverId) => $"priorauthbot:exception-inbox:{approverId}";
}

/// <summary>
/// Agent Framework's checkpoints, as JSON in an <see cref="IDistributedCache"/> (chapter 11). A
/// workflow that waits days for a manager cannot wait in memory: the API restarts, scales out, and
/// deploys. Each checkpoint holds the workflow's messages and state, including the requester's words,
/// so it gets the same protection and lifetime as the run record, and is deleted with it.
/// </summary>
public sealed class DistributedCheckpointStore(IDistributedCache cache, IOptionsMonitor<AgentOptions> options) : JsonCheckpointStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var checkpoint = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
        var expiry = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.CurrentValue.ExceptionRequestLifetime + TimeSpan.FromDays(1) };
        await cache.SetAsync(Key(sessionId, checkpoint.CheckpointId), JsonSerializer.SerializeToUtf8Bytes(value, Json), expiry).ConfigureAwait(false);

        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var index = await IndexAsync(sessionId).ConfigureAwait(false);
            index.Add(new IndexEntry(checkpoint.CheckpointId, parent?.CheckpointId));
            await cache.SetAsync(IndexKey(sessionId), JsonSerializer.SerializeToUtf8Bytes(index, Json), expiry).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return checkpoint;
    }

    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bytes = await cache.GetAsync(Key(sessionId, key.CheckpointId)).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Checkpoint {key.CheckpointId} of run {sessionId} is gone (expired or deleted).");
        return JsonSerializer.Deserialize<JsonElement>(bytes, Json);
    }

    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null) =>
        (await IndexAsync(sessionId).ConfigureAwait(false))
            .Where(e => withParent is null || e.Parent == withParent.CheckpointId)
            .Select(e => new CheckpointInfo(sessionId, e.Id));

    /// <summary>Deletes every checkpoint of a run, once it has ended.</summary>
    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var entry in await IndexAsync(sessionId).ConfigureAwait(false))
        {
            await cache.RemoveAsync(Key(sessionId, entry.Id), cancellationToken).ConfigureAwait(false);
        }

        await cache.RemoveAsync(IndexKey(sessionId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<IndexEntry>> IndexAsync(string sessionId)
    {
        var bytes = await cache.GetAsync(IndexKey(sessionId)).ConfigureAwait(false);
        return bytes is null ? [] : JsonSerializer.Deserialize<List<IndexEntry>>(bytes, Json) ?? [];
    }

    private static string Key(string sessionId, string id) => $"priorauthbot:checkpoint:{sessionId}:{id}";

    private static string IndexKey(string sessionId) => $"priorauthbot:checkpoint-index:{sessionId}";

    private sealed record IndexEntry(string Id, string? Parent);
}

/// <summary>A decided exception, as HR would file it.</summary>
public sealed record ExceptionDecision(string Id, string RunId, string RequesterId, string ApproverId, string PolicyNumber, double HoursAboveLimit, bool Approved, string? Comment, DateTimeOffset DecidedAt);

/// <summary>
/// Where decided exceptions are filed (chapter 11). The book keeps them in memory; a real one is the
/// HR system's API, called on behalf of the approving manager, as chapter 10's HR client does for a balance.
/// </summary>
public interface IExceptionRegister
{
    /// <summary>Files a decision. The same <paramref name="runId"/> always returns the same decision, so a retried step files it once.</summary>
    ExceptionDecision Record(string runId, ExceptionDraft draft, ApproverAnswer answer, DateTimeOffset decidedAt);

    IReadOnlyList<ExceptionDecision> All { get; }
}

public sealed class InMemoryExceptionRegister : IExceptionRegister
{
    private readonly ConcurrentDictionary<string, ExceptionDecision> _byRun = new(StringComparer.Ordinal);
    private int _next = 2_000;

    public IReadOnlyList<ExceptionDecision> All => [.. _byRun.Values.OrderBy(d => d.Id, StringComparer.Ordinal)];

    public ExceptionDecision Record(string runId, ExceptionDraft draft, ApproverAnswer answer, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(answer);
        return _byRun.GetOrAdd(runId, _ => new ExceptionDecision(
            $"EXC-{Interlocked.Increment(ref _next)}", runId, draft.RequesterId, answer.DecidedBy, draft.PolicyNumber, draft.HoursAboveLimit, answer.Approve, answer.Comment, decidedAt));
    }
}
