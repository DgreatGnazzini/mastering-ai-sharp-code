using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Agents;
using PriorAuthBot.Core.Agents.Exceptions;
using PriorAuthBot.Core.Tools;

namespace PriorAuthBot.Eval;

/// <summary>Chapter 11's agent cases (eval/datasets/agent-cases.json).</summary>
internal sealed record AgentCaseSet(string Description, List<AgentCase> Cases)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AgentCaseSet Load() =>
        JsonSerializer.Deserialize<AgentCaseSet>(File.ReadAllText(EvalPaths.AgentCases), JsonOptions)
        ?? throw new InvalidOperationException($"{EvalPaths.AgentCases} is empty.");
}

internal sealed record AgentCase(
    string Id,
    string Kind,
    string User,
    string? Message = null,
    string? ExpectStatus = null,
    string? ExpectStopCode = null,
    double? ExpectHours = null,
    string? ExpectApprover = null,
    string[]? Turns = null,
    string[]? ExpectTools = null,
    bool ExpectApproval = false,
    string[]? AnswerMustContain = null,
    string[]? AnswerMustNotContain = null)
{
    public const string Exception = "exception";
    public const string Conversation = "conversation";
    public const string AnyTool = "any";
}

internal sealed record AgentCaseResult(
    AgentCase Case,
    bool? OutcomeOk,
    bool? HoursOk,
    int TurnsScored,
    int TurnsCorrect,
    bool ApprovalOk,
    string Summary,
    int ModelCalls,
    decimal Cost,
    bool WithinLimits,
    IReadOnlyList<string> Leaks,
    IReadOnlyList<string> Missing,
    string? Error,
    TimeSpan Elapsed);

/// <summary>The numbers the agent report and the gate use.</summary>
internal sealed record AgentSummary(
    int Exceptions,
    int OutcomesCorrect,
    int WithHours,
    int HoursCorrect,
    int TurnsScored,
    int TurnsCorrect,
    int ExpectedApprovals,
    int ApprovalsHeld,
    int DecidedWithoutApprover,
    int OverLimits,
    int Leaks,
    int Errors,
    int MaxModelCalls,
    decimal MaxCost)
{
    public double Outcome => Exceptions == 0 ? 1 : (double)OutcomesCorrect / Exceptions;

    public double Hours => WithHours == 0 ? 1 : (double)HoursCorrect / WithHours;

    public double Tools => TurnsScored == 0 ? 1 : (double)TurnsCorrect / TurnsScored;

    public static AgentSummary Of(IReadOnlyList<AgentCaseResult> results, int decisionsFiled)
    {
        ArgumentNullException.ThrowIfNull(results);
        var exceptions = results.Where(r => r.Case.Kind == AgentCase.Exception).ToList();
        return new AgentSummary(
            exceptions.Count,
            exceptions.Count(r => r.OutcomeOk == true),
            exceptions.Count(r => r.Case.ExpectHours is not null),
            exceptions.Count(r => r.HoursOk == true),
            results.Sum(r => r.TurnsScored),
            results.Sum(r => r.TurnsCorrect),
            results.Count(r => r.Case.ExpectApproval),
            results.Count(r => r.Case.ExpectApproval && r.ApprovalOk),
            decisionsFiled,
            results.Count(r => !r.WithinLimits),
            results.Sum(r => r.Leaks.Count),
            results.Count(r => r.Error is not null),
            results.Count == 0 ? 0 : results.Max(r => r.ModelCalls),
            results.Count == 0 ? 0 : results.Max(r => r.Cost));
    }
}

/// <summary>
/// The agent cases (chapter 11). Exception requests run through <see cref="ExceptionRequestService"/>
/// as their user and are never answered, so any decision in the register at the end was filed
/// without the approver, and that fails the run like a leak. Conversations run through
/// <see cref="PolicyAgentService"/>, one turn at a time, in one conversation per case.
/// </summary>
internal static class AgentEvaluation
{
    /// <summary>The eval's clock: 14 November 2026, before HR-104's deadline, so the cases mean the same thing in December.</summary>
    public static readonly DateTimeOffset Today = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Agent state in its own memory cache, apart from the eval's on-disk response cache, and the
    /// workflow on the eval's clock. A CPU runner needs about a minute per model call, so the time
    /// limit is longer than the API's two minutes; the step limit and spend cap are the API's own.
    /// </summary>
    public static void Configure(IServiceCollection services, bool slowModel)
    {
        ArgumentNullException.ThrowIfNull(services);
        var state = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        services.Replace(ServiceDescriptor.Singleton(sp => new AgentConversationStore(state, sp.GetRequiredService<IOptionsMonitor<AgentOptions>>())));
        services.Replace(ServiceDescriptor.Singleton(sp => new ExceptionRunStore(state, sp.GetRequiredService<IOptionsMonitor<AgentOptions>>())));
        services.Replace(ServiceDescriptor.Singleton(sp => new DistributedCheckpointStore(state, sp.GetRequiredService<IOptionsMonitor<AgentOptions>>())));
        services.Replace(ServiceDescriptor.Singleton<IExceptionRegister>(new InMemoryExceptionRegister()));
        services.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<ExceptionRequestService>(sp, (TimeProvider)new FixedClock(Today))));
        if (slowModel)
        {
            services.PostConfigure<AgentOptions>(o => o.MaxRunDuration = TimeSpan.FromMinutes(20));
        }
    }

    public static async Task<IReadOnlyList<AgentCaseResult>> RunAsync(EvalHost host, GoldenSet set, AgentCaseSet cases, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(cases);
        var limits = host.Get<IOptionsMonitor<AgentOptions>>().CurrentValue;
        var results = new List<AgentCaseResult>();
        foreach (var c in cases.Cases)
        {
            var timer = Stopwatch.StartNew();
            var caller = new ToolCaller(set.Caller(c.User));
            AgentCaseResult result;
            try
            {
                result = c.Kind == AgentCase.Exception
                    ? await ExceptionAsync(host.Get<ExceptionRequestService>(), c, caller, limits, cancellationToken).ConfigureAwait(false)
                    : await ConversationAsync(host.Get<PolicyAgentService>(), c, caller, limits, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = new AgentCaseResult(c, c.Kind == AgentCase.Exception ? false : null, null, c.ExpectTools?.Count(t => t != AgentCase.AnyTool) ?? 0, 0, !c.ExpectApproval, "error", 0, 0, true, [], [], $"{ex.GetType().Name}: {ex.Message}", default);
            }

            results.Add(result with { Elapsed = timer.Elapsed });
            progress?.Invoke($"{c.Id}: {result.Summary}, {timer.Elapsed.TotalSeconds:0.0} s");
        }

        return results;
    }

    private static async Task<AgentCaseResult> ExceptionAsync(ExceptionRequestService workflow, AgentCase c, ToolCaller caller, AgentOptions limits, CancellationToken cancellationToken)
    {
        var run = await workflow.StartAsync(c.Message!, caller, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agents are turned off.");
        var statusOk = string.Equals(run.Status.ToString(), c.ExpectStatus, StringComparison.Ordinal)
            && (c.ExpectStopCode is null || run.StopCode == c.ExpectStopCode)
            && (c.ExpectApprover is null || run.Draft?.Approver.UserId == c.ExpectApprover);
        bool? hoursOk = c.ExpectHours is { } hours ? run.Draft is not null && Math.Abs(run.Draft.RequestedHours - hours) < 0.01 : null;
        var text = $"{run.Message}\n{run.Draft?.Justification}";
        return new AgentCaseResult(
            c, statusOk, hoursOk, 0, 0, true,
            $"{run.Status}{(run.StopCode is null ? string.Empty : $" ({run.StopCode})")}{(run.Draft is null ? string.Empty : $", {run.Draft.RequestedHours:0.##} h to {run.Draft.Approver.UserId}")}",
            run.ModelCalls, run.Cost, run.ModelCalls <= limits.MaxModelCalls && run.Cost <= limits.MaxCostUsd,
            Leaks(c, text), Missing(c, text), null, default);
    }

    private static async Task<AgentCaseResult> ConversationAsync(PolicyAgentService agent, AgentCase c, ToolCaller caller, AgentOptions limits, CancellationToken cancellationToken)
    {
        string? id = null;
        AgentReply? last = null;
        var scored = 0;
        var correct = 0;
        var calls = new List<string>();
        var within = true;
        var modelCalls = 0;
        decimal cost = 0;
        var turns = c.Turns ?? [];
        for (var i = 0; i < turns.Length; i++)
        {
            var result = await agent.RunAsync(turns[i], id, caller, cancellationToken).ConfigureAwait(false);
            last = result.Reply ?? throw new InvalidOperationException($"Turn {i + 1} was refused: {result.Refusal}.");
            id = last.ConversationId;
            var first = last.ToolCalls.Count > 0 ? last.ToolCalls[0].Name : ToolCase.None;
            calls.Add(first);
            var expected = c.ExpectTools?.ElementAtOrDefault(i);
            if (expected is not null and not AgentCase.AnyTool)
            {
                scored++;
                correct += first == expected ? 1 : 0;
            }

            modelCalls = Math.Max(modelCalls, last.Usage.ModelCalls);
            cost = Math.Max(cost, last.Usage.Cost);
            within &= last.Usage.ModelCalls <= limits.MaxModelCalls && last.Usage.Cost <= limits.MaxCostUsd;
        }

        var answer = last?.Answer ?? string.Empty;
        var approvalOk = !c.ExpectApproval || last?.PendingActions.Count > 0;
        return new AgentCaseResult(c, null, null, scored, correct, approvalOk, string.Join(" -> ", calls), modelCalls, cost, within, Leaks(c, answer), Missing(c, answer), null, default);
    }

    private static List<string> Leaks(AgentCase c, string text) =>
        [.. (c.AnswerMustNotContain ?? []).Where(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(f => $"{c.Id}: the answer for {c.User} contains \"{f}\"")];

    private static List<string> Missing(AgentCase c, string text) =>
        [.. (c.AnswerMustContain ?? []).Where(f => !text.Contains(f, StringComparison.OrdinalIgnoreCase))];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
