using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Core.Agents.Exceptions;

/// <summary>What a requester or an approver sees of a run. Nobody else sees anything.</summary>
public sealed record ExceptionRunView(
    string Id,
    ExceptionStatus Status,
    string? StopCode,
    string Message,
    string RequesterName,
    string? ApproverName,
    ExceptionDraft? Draft,
    IReadOnlyList<ExceptionRunStep> Steps,
    int ModelCalls,
    decimal Cost,
    DateTimeOffset ExpiresAt,
    string YourRole)
{
    public static ExceptionRunView Of(ExceptionRun run, string viewerId)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(run.Id, run.Status, run.StopCode, run.Message, run.RequesterName, run.ApproverName, run.Draft, run.Steps,
            run.ModelCalls, Math.Round(run.Cost, 6), run.ExpiresAt, run.RequesterId == viewerId ? "requester" : "approver");
    }
}

/// <summary>
/// Runs the policy exception workflow for PriorAuthBot's API (chapter 11). Starting a run executes
/// it up to the first person who must answer, checkpoints it, and returns. Each answer comes in a
/// later request, from the one person allowed to give it, and resumes the run from its last
/// checkpoint, in this API instance or another one. A run that nobody answers expires.
/// </summary>
public sealed partial class ExceptionRequestService(
    PolicyAgentFactory agents,
    SecureRetriever retriever,
    HrClient hr,
    IExceptionRegister register,
    ExceptionRunStore runs,
    DistributedCheckpointStore checkpoints,
    ILogger<ExceptionRequestService> logger,
    TimeProvider? time = null)
{
    // One answer at a time per run in this instance. Across instances, chapter 14 adds a lease.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Starts a request for <paramref name="requester"/>. Returns null when agents are turned off.</summary>
    public async Task<ExceptionRunView?> StartAsync(string message, ToolCaller requester, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(requester);
        if (!agents.Limits.Enabled)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var run = new ExceptionRun
        {
            Id = ExceptionRunStore.NewId(),
            RequesterId = requester.UserId,
            RequesterName = requester.Identity.DisplayName,
            Status = ExceptionStatus.Running,
            CreatedAt = now,
            ExpiresAt = now + agents.Limits.ExceptionRequestLifetime,
        };
        LogStarted(logger, run.Id, requester.UserId);

        var budget = agents.NewBudget(run.Id, requester.UserId);
        var workflow = Build(run.Id, requester, budget);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(agents.Limits.MaxRunDuration);
        try
        {
            await using var stream = await InProcessExecution.RunStreamingAsync(workflow, new ExceptionRequestInput(message), Manager(), run.Id, timeout.Token).ConfigureAwait(false);
            run = await DriveAsync(run, stream, requester.UserId, budget, answer: null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            budget.StopForTime();
            run = Ended(run, new ExceptionOutcome(ExceptionStatus.Stopped, StopCodes.Limit, budget.StopReason!), requester.UserId, budget);
        }

        await SaveAsync(run, cancellationToken).ConfigureAwait(false);
        return ExceptionRunView.Of(run, requester.UserId);
    }

    /// <summary>The requester sends the draft to the approver, or withdraws it. Null means "no such request for you".</summary>
    public Task<ExceptionRunView?> SubmitAsync(string runId, ToolCaller requester, bool submit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        return AnswerAsync(runId, requester.UserId, ExceptionStatus.WaitingForRequester, r => r.RequesterId, new RequesterAnswer(submit), cancellationToken);
    }

    /// <summary>The approver decides. Null means "no such request for you".</summary>
    public Task<ExceptionRunView?> DecideAsync(string runId, ToolCaller approver, bool approve, string? comment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var trimmed = comment?.Trim();
        if (trimmed is { Length: > 500 })
        {
            throw new ArgumentException("A comment is at most 500 characters.", nameof(comment));
        }

        return AnswerAsync(runId, approver.UserId, ExceptionStatus.WaitingForApprover, r => r.ApproverId, new ApproverAnswer(approve, trimmed, approver.UserId), cancellationToken);
    }

    /// <summary>The requester withdraws a request that is still waiting, at either step.</summary>
    public async Task<ExceptionRunView?> WithdrawAsync(string runId, ToolCaller requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        var gate = Gates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await GetCurrentAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.RequesterId != requester.UserId || !run.IsWaiting)
            {
                return null;
            }

            run = run with
            {
                Status = ExceptionStatus.Withdrawn,
                Message = "You withdrew the request.",
                PendingPort = null,
                PendingRequestId = null,
                Steps = [.. run.Steps, new ExceptionRunStep(_time.GetUtcNow(), "withdraw", requester.UserId, null)],
            };
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            return ExceptionRunView.Of(run, requester.UserId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A run, for its requester or its approver only.</summary>
    public async Task<ExceptionRunView?> GetAsync(string runId, ToolCaller viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var run = await GetCurrentAsync(runId, cancellationToken).ConfigureAwait(false);
        return run is not null && (run.RequesterId == viewer.UserId || run.ApproverId == viewer.UserId) ? ExceptionRunView.Of(run, viewer.UserId) : null;
    }

    /// <summary>The requests waiting for <paramref name="approver"/> to decide.</summary>
    public async Task<IReadOnlyList<ExceptionRunView>> ReviewsAsync(ToolCaller approver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var waiting = new List<ExceptionRunView>();
        foreach (var run in await runs.ForApproverAsync(approver.UserId, cancellationToken).ConfigureAwait(false))
        {
            var current = Expire(run);
            if (current.Status == ExceptionStatus.WaitingForApprover)
            {
                waiting.Add(ExceptionRunView.Of(current, approver.UserId));
            }
        }

        return waiting;
    }

    private async Task<ExceptionRunView?> AnswerAsync<TAnswer>(
        string runId, string actorId, ExceptionStatus expected, Func<ExceptionRun, string?> allowed, TAnswer answer, CancellationToken cancellationToken)
        where TAnswer : notnull
    {
        var gate = Gates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The same "nothing here" for a missing run, someone else's run, a run at another step,
            // and an expired one, so a run ID tells nobody else anything.
            var run = await GetCurrentAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.Status != expected || allowed(run) != actorId || run.CheckpointId is null)
            {
                return null;
            }

            LogAnswered(logger, run.Id, actorId, run.PendingPort ?? "?");

            // A resumed stretch makes no model calls, but it still runs under a budget, so the kill
            // switch and the time limit apply to it like any other.
            var budget = agents.NewBudget(run.Id, actorId);
            var workflow = Build(run.Id, requester: null, budget);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(agents.Limits.MaxRunDuration);
            await using var stream = await InProcessExecution.ResumeStreamingAsync(workflow, new CheckpointInfo(run.Id, run.CheckpointId), Manager(), timeout.Token).ConfigureAwait(false);
            run = await DriveAsync(run, stream, actorId, budget, answer, timeout.Token).ConfigureAwait(false);
            await SaveAsync(run, cancellationToken).ConfigureAwait(false);
            return ExceptionRunView.Of(run, actorId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Watches one stretch of the run until it waits for a person or ends, and records each step.
    /// On a resumed run the pending request is published again first; that is where the answer goes in.
    /// </summary>
    private async Task<ExceptionRun> DriveAsync(ExceptionRun run, StreamingRun stream, string actorId, AgentBudget budget, object? answer, CancellationToken cancellationToken)
    {
        var steps = run.Steps.ToList();
        ExceptionOutcome? outcome = null;
        string? failure = null;
        bool watchAgain;
        do
        {
            watchAgain = false;
            await foreach (var e in stream.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken).ConfigureAwait(false))
            {
                switch (e)
                {
                    case RequestInfoEvent info when answer is not null && info.Request.RequestId == run.PendingRequestId:
                        await stream.SendResponseAsync(info.Request.CreateResponse(answer)).ConfigureAwait(false);
                        steps.Add(new ExceptionRunStep(_time.GetUtcNow(), info.Request.PortInfo.PortId, actorId, Describe(answer)));
                        answer = null;
                        watchAgain = true;
                        break;

                    case RequestInfoEvent info when info.Request.TryGetDataAs<ExceptionDraft>(out var draft):
                        // The approver comes from step 2's HR lookup, carried in the draft code built.
                        run = run with
                        {
                            Status = ExceptionStatus.WaitingForRequester,
                            Message = $"Review the draft, then send it to {draft.Approver.Name} or withdraw it.",
                            Draft = draft,
                            ApproverId = draft.Approver.UserId,
                            ApproverName = draft.Approver.Name,
                            PendingPort = info.Request.PortInfo.PortId,
                            PendingRequestId = info.Request.RequestId,
                        };
                        break;

                    case RequestInfoEvent info when info.Request.IsDataOfType<ApproverReview>():
                        run = run with
                        {
                            Status = ExceptionStatus.WaitingForApprover,
                            Message = $"Sent to {run.ApproverName}, who decides.",
                            PendingPort = info.Request.PortInfo.PortId,
                            PendingRequestId = info.Request.RequestId,
                        };
                        break;

                    case ExecutorCompletedEvent done when done.ExecutorId is not (ExceptionWorkflow.RequesterPort or ExceptionWorkflow.ApproverPort):
                        steps.Add(new ExceptionRunStep(_time.GetUtcNow(), done.ExecutorId, actorId, null));
                        break;

                    case WorkflowOutputEvent output when output.Is<ExceptionOutcome>(out var result):
                        outcome = result;
                        break;

                    case ExecutorFailedEvent failed:
                        failure = failed.ExecutorId;
                        LogStepFailed(logger, run.Id, failed.ExecutorId, failed.Data as Exception);
                        break;

                    case WorkflowErrorEvent error:
                        failure ??= "workflow";
                        LogStepFailed(logger, run.Id, "workflow", error.Exception);
                        break;
                }
            }
        }
        while (watchAgain);

        var usage = budget.Usage();
        foreach (var model in usage.Steps.Where(s => s.Kind == "model"))
        {
            steps.Add(new ExceptionRunStep(model.At, "model call", actorId, string.Create(CultureInfo.InvariantCulture, $"{model.InputTokens} in, {model.OutputTokens} out, ${model.Cost:0.000000}")));
        }

        run = run with
        {
            Steps = [.. steps.OrderBy(s => s.At)],
            ModelCalls = run.ModelCalls + usage.ModelCalls,
            Cost = run.Cost + usage.Cost,
            CheckpointId = stream.LastCheckpoint?.CheckpointId ?? run.CheckpointId,
        };

        if (failure is not null)
        {
            return Ended(run, new ExceptionOutcome(ExceptionStatus.Failed, null, "Something went wrong, and nothing was filed. Try again later."), actorId, budget: null);
        }

        return outcome is null ? run : Ended(run, outcome, actorId, budget: null);
    }

    // The answer, in a word. An approver's comment stays in the decision record, not in the trace.
    private static string Describe(object answer) => answer switch
    {
        RequesterAnswer { Submit: true } => "sent to the approver",
        RequesterAnswer => "withdrew",
        ApproverAnswer { Approve: true } => "approved",
        ApproverAnswer => "declined",
        _ => "answered",
    };

    private ExceptionRun Ended(ExceptionRun run, ExceptionOutcome outcome, string actorId, AgentBudget? budget)
    {
        LogEnded(logger, run.Id, actorId, outcome.Status, outcome.StopCode ?? "-");
        return run with
        {
            Status = outcome.Status,
            StopCode = outcome.StopCode,
            Message = outcome.Message,
            DecisionId = outcome.DecisionId,
            PendingPort = null,
            PendingRequestId = null,
            ModelCalls = budget is null ? run.ModelCalls : run.ModelCalls + budget.Usage().ModelCalls,
            Cost = budget is null ? run.Cost : run.Cost + budget.Usage().Cost,
        };
    }

    private async Task SaveAsync(ExceptionRun run, CancellationToken cancellationToken)
    {
        await runs.SaveAsync(run, cancellationToken).ConfigureAwait(false);

        // A run that has ended keeps its record (the trace) but not its checkpoints, which hold the
        // requester's words and the workflow's state.
        if (!run.IsWaiting)
        {
            await checkpoints.DeleteAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExceptionRun?> GetCurrentAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var current = Expire(run);
        if (current != run)
        {
            await SaveAsync(current, cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    private ExceptionRun Expire(ExceptionRun run) =>
        run.IsWaiting && run.ExpiresAt <= _time.GetUtcNow()
            ? run with { Status = ExceptionStatus.Expired, Message = "Nobody answered in time, so the request expired. Nothing was filed.", PendingPort = null, PendingRequestId = null }
            : run;

    private Workflow Build(string runId, ToolCaller? requester, AgentBudget budget) =>
        ExceptionWorkflow.Build(new ExceptionRunContext(runId, requester, budget, _time), agents, retriever, hr, register);

    private CheckpointManager Manager() => CheckpointManager.CreateJson(checkpoints);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exception run {RunId} started for {UserId}.")]
    private static partial void LogStarted(ILogger logger, string runId, string userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exception run {RunId}: {UserId} answered at {Port}.")]
    private static partial void LogAnswered(ILogger logger, string runId, string userId, string port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exception run {RunId} ended for {UserId}: {Status} ({StopCode}).")]
    private static partial void LogEnded(ILogger logger, string runId, string userId, ExceptionStatus status, string stopCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Exception run {RunId}: step {Step} failed.")]
    private static partial void LogStepFailed(ILogger logger, string runId, string step, Exception? exception);
}
