using PriorAuthBot.Core.Retrieval;

namespace PriorAuthBot.Core.Usage;

/// <summary>How a request reached a document, for the audit log.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AccessKind>))]
public enum AccessKind
{
    /// <summary>Returned by retrieval: its text went into a prompt or a search result.</summary>
    Retrieved,

    /// <summary>Cited by the answer the user was shown.</summary>
    Cited,

    /// <summary>Opened whole, through a citation link or the lookup_policy tool.</summary>
    Viewed,
}

/// <summary>One document a request touched, as the audit log records it: IDs and labels, never text.</summary>
public sealed record TouchedDocument(string DocumentId, string Title, string Sensitivity, bool ContainsPhi, AccessKind Access, IReadOnlyList<string> ChunkIds);

/// <summary>
/// Everything chapter 13 needs to know about one API request while it runs: who is asking, which
/// documents retrieval returned, what the model calls used, and what went back. The API opens one
/// for every request (<see cref="Begin"/>), and the code that already sits on the only paths to
/// documents and models fills it in: <see cref="SecureRetriever"/> for documents and
/// <see cref="MeteringChatClient"/> for tokens. A new endpoint, tool, or agent step cannot forget to
/// be audited or metered, because it cannot reach a document or a model any other way.
/// </summary>
/// <remarks>
/// It flows with the async call, like <see cref="System.Diagnostics.Activity.Current"/>. Code that
/// runs outside a request, such as ingestion, sees <see cref="Current"/> as null.
/// </remarks>
public sealed class AiRequestContext
{
    private static readonly AsyncLocal<AiRequestContext?> Ambient = new();

    private readonly Lock _gate = new();
    private readonly Dictionary<string, TouchedDocument> _documents = new(StringComparer.Ordinal);
    private long _inputTokens;
    private long _outputTokens;
    private decimal _cost;
    private int _modelCalls;

    private AiRequestContext(string auditId, string operation, string userId, string displayName)
    {
        AuditId = auditId;
        Operation = operation;
        UserId = userId;
        DisplayName = displayName;
    }

    /// <summary>The request in progress on this async flow, or null outside a request.</summary>
    public static AiRequestContext? Current => Ambient.Value;

    /// <summary>The audit record's ID. Spans carry it as <c>priorauthbot.audit.id</c>, and responses as a header.</summary>
    public string AuditId { get; }

    /// <summary>The endpoint, such as <c>POST /ask</c>.</summary>
    public string Operation { get; }

    public string UserId { get; }

    public string DisplayName { get; }

    /// <summary>The caller's groups, once a service has resolved them. The permission set the answer was built for.</summary>
    public IReadOnlyList<string> Groups { get; private set; } = [];

    /// <summary>What the user sent. Goes to the audit log, which is access-controlled, and nowhere else.</summary>
    public string? Prompt { get; private set; }

    /// <summary>What the user was shown.</summary>
    public string? Response { get; private set; }

    /// <summary>True when the answer came from the answer cache, with no retrieval or model call.</summary>
    public bool AnswerCacheHit { get; private set; }

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public int ModelCalls => Volatile.Read(ref _modelCalls);

    public decimal Cost
    {
        get
        {
            lock (_gate)
            {
                return _cost;
            }
        }
    }

    /// <summary>Starts a request on this async flow. Dispose the result when the request ends.</summary>
    public static Scope Begin(string operation, string userId, string displayName, string? auditId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var context = new AiRequestContext(auditId ?? Guid.CreateVersion7().ToString("N"), operation, userId, displayName ?? string.Empty);
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(context, previous);
    }

    public void SetCaller(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Groups = [.. caller.Groups.Order(StringComparer.Ordinal)];
    }

    public void SetPrompt(string? prompt) => Prompt = prompt;

    public void SetResponse(string? response) => Response = response;

    public void MarkAnswerCacheHit() => AnswerCacheHit = true;

    /// <summary>Records chunks that reached this request. A document seen more than one way keeps its strongest access.</summary>
    public void RecordDocuments(IEnumerable<ChunkRecord> chunks, AccessKind access)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        lock (_gate)
        {
            foreach (var group in chunks.GroupBy(c => c.DocumentId, StringComparer.Ordinal))
            {
                var first = group.First();
                var chunkIds = group.Select(c => c.ChunkId);
                var strongest = access;
                if (_documents.TryGetValue(group.Key, out var existing))
                {
                    chunkIds = existing.ChunkIds.Concat(chunkIds);
                    strongest = (AccessKind)Math.Max((int)existing.Access, (int)access);
                }

                _documents[group.Key] = new TouchedDocument(first.DocumentId, first.Title, first.Sensitivity, first.ContainsPhi, strongest,
                    [.. chunkIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
            }
        }
    }

    /// <summary>Marks documents the answer cites. Only ones already recorded: citing never adds a document.</summary>
    public void MarkCited(IEnumerable<string> documentIds)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        lock (_gate)
        {
            foreach (var id in documentIds)
            {
                if (_documents.TryGetValue(id, out var existing) && existing.Access == AccessKind.Retrieved)
                {
                    _documents[id] = existing with { Access = AccessKind.Cited };
                }
            }
        }
    }

    /// <summary>Records a cached answer's documents, which the user is shown again without a new retrieval.</summary>
    public void RecordDocuments(IEnumerable<TouchedDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        lock (_gate)
        {
            foreach (var document in documents)
            {
                _documents.TryAdd(document.DocumentId, document);
            }
        }
    }

    public IReadOnlyList<TouchedDocument> Documents
    {
        get
        {
            lock (_gate)
            {
                return [.. _documents.Values.OrderBy(d => d.DocumentId, StringComparer.Ordinal)];
            }
        }
    }

    public void RecordUsage(long inputTokens, long outputTokens, decimal cost)
    {
        Interlocked.Add(ref _inputTokens, inputTokens);
        Interlocked.Add(ref _outputTokens, outputTokens);
        Interlocked.Increment(ref _modelCalls);
        lock (_gate)
        {
            _cost += cost;
        }
    }

    /// <summary>Ends the request and restores whatever context was there before.</summary>
    public sealed class Scope(AiRequestContext context, AiRequestContext? previous) : IDisposable
    {
        public AiRequestContext Context { get; } = context;

        public void Dispose() => Ambient.Value = previous;
    }
}
