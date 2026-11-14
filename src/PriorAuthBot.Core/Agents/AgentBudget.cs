using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.AI;

namespace PriorAuthBot.Core.Agents;

/// <summary>Why a run stopped before it finished.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AgentLimit>))]
public enum AgentLimit
{
    StepLimit,
    ToolLimit,
    SpendCap,
    TimeLimit,
    Disabled,
}

/// <summary>One step of a run, for the trace and the log. It never holds the prompt, the arguments, or the answer.</summary>
public sealed record AgentStep(int Number, string Kind, string Name, int InputTokens, int OutputTokens, decimal Cost, DateTimeOffset At);

/// <summary>What a run used, against its limits.</summary>
public sealed record AgentUsage(int ModelCalls, int ToolCalls, long InputTokens, long OutputTokens, decimal Cost, decimal SpendCap, IReadOnlyList<AgentStep> Steps);

/// <summary>Thrown by <see cref="BudgetedChatClient"/> when a run reaches one of its limits.</summary>
public sealed class AgentLimitExceededException(AgentLimit limit, string message) : Exception(message)
{
    public AgentLimit Limit { get; } = limit;
}

/// <summary>
/// The budget of one agent run (chapter 11): a step limit, a tool-call limit, a spend cap, and the
/// kill switch. The model cannot see it or change it. <see cref="BudgetedChatClient"/> checks it
/// before every model call and <see cref="BudgetedFunction"/> before every tool call; when a limit
/// is reached, the run stops, and nothing the model writes can ask for more.
/// </summary>
public sealed partial class AgentBudget
{
    private readonly AgentOptions _limits;
    private readonly Func<bool> _enabled;
    private readonly TokenCounter _tokens;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ModelPrice _price;
    private readonly List<AgentStep> _steps = [];
    private readonly Lock _gate = new();
    private int _modelCalls;
    private int _toolCalls;
    private long _inputTokens;
    private long _outputTokens;
    private decimal _cost;

    public AgentBudget(string runId, string userId, AgentOptions limits, Func<bool> enabled, TokenCounter tokens, ILogger logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);
        RunId = runId;
        UserId = userId;
        _limits = limits;
        _enabled = enabled;
        _tokens = tokens;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _price = new ModelPrice("agent-budget", limits.InputPricePerMillion, limits.OutputPricePerMillion);
    }

    public string RunId { get; }

    public string UserId { get; }

    /// <summary>Set when a limit stopped the run.</summary>
    public AgentLimit? StoppedBy { get; private set; }

    public string? StopReason { get; private set; }

    /// <summary>
    /// Called before a model call. Throws when the run is already stopped, the kill switch is off,
    /// the step limit is reached, or the call's input alone would go over the spend cap. Returns the
    /// estimated input tokens, for when the provider reports no usage.
    /// </summary>
    public int BeforeModelCall(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var estimate = EstimateInputTokens(messages, options);
        lock (_gate)
        {
            ThrowIfStopped();
            if (!_enabled())
            {
                Stop(AgentLimit.Disabled, "Agents are turned off (Agents:Enabled is false).");
            }

            if (_modelCalls >= _limits.MaxModelCalls)
            {
                Stop(AgentLimit.StepLimit, $"The run reached its step limit of {_limits.MaxModelCalls} model calls.");
            }

            if (_cost + CostModel.Cost(_price, estimate, 0) > _limits.MaxCostUsd)
            {
                Stop(AgentLimit.SpendCap, $"The next model call would go over the run's spend cap of ${_limits.MaxCostUsd:0.0000}.");
            }

            _modelCalls++;
        }

        return estimate;
    }

    /// <summary>
    /// Called after a model call, with the provider's token counts, or estimates when it reports none.
    /// A run that went over the cap on this call stops before the next one.
    /// </summary>
    public void AfterModelCall(ChatResponse response, int estimatedInputTokens)
    {
        ArgumentNullException.ThrowIfNull(response);
        var input = (int?)response.Usage?.InputTokenCount ?? estimatedInputTokens;
        var output = (int?)response.Usage?.OutputTokenCount ?? EstimateOutputTokens(response);
        var cost = CostModel.Cost(_price, input, output);
        AgentStep step;
        lock (_gate)
        {
            _inputTokens += input;
            _outputTokens += output;
            _cost += cost;
            step = new AgentStep(_steps.Count + 1, "model", "chat", input, output, cost, _time.GetUtcNow());
            _steps.Add(step);
        }

        LogModelStep(_logger, RunId, UserId, step.Number, input, output, cost);
    }

    /// <summary>
    /// Called before a tool call. Returns false, with the reason, when the run may not make it: the
    /// kill switch is off, or the tool-call limit is reached. The run then stops.
    /// </summary>
    public bool TryStartToolCall(string toolName, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        AgentStep step;
        lock (_gate)
        {
            if (StoppedBy is not null)
            {
                reason = StopReason;
                return false;
            }

            if (!_enabled())
            {
                MarkStopped(AgentLimit.Disabled, "Agents are turned off (Agents:Enabled is false).");
                reason = StopReason;
                return false;
            }

            if (_toolCalls >= _limits.MaxToolCalls)
            {
                MarkStopped(AgentLimit.ToolLimit, $"The run reached its limit of {_limits.MaxToolCalls} tool calls.");
                reason = StopReason;
                return false;
            }

            _toolCalls++;
            step = new AgentStep(_steps.Count + 1, "tool", toolName, 0, 0, 0, _time.GetUtcNow());
            _steps.Add(step);
        }

        LogToolStep(_logger, RunId, UserId, step.Number, toolName);
        reason = null;
        return true;
    }

    /// <summary>Records a step that is not a model or tool call, such as a code step of a workflow.</summary>
    public void RecordStep(string kind, string name)
    {
        lock (_gate)
        {
            _steps.Add(new AgentStep(_steps.Count + 1, kind, name, 0, 0, 0, _time.GetUtcNow()));
        }
    }

    /// <summary>Marks the run stopped by its time limit.</summary>
    public void StopForTime()
    {
        lock (_gate)
        {
            if (StoppedBy is null)
            {
                MarkStopped(AgentLimit.TimeLimit, $"The run reached its time limit of {_limits.MaxRunDuration.TotalSeconds:0} seconds.");
            }
        }
    }

    public AgentUsage Usage()
    {
        lock (_gate)
        {
            return new AgentUsage(_modelCalls, _toolCalls, _inputTokens, _outputTokens, _cost, _limits.MaxCostUsd, [.. _steps]);
        }
    }

    private void ThrowIfStopped()
    {
        if (StoppedBy is { } limit)
        {
            throw new AgentLimitExceededException(limit, StopReason!);
        }
    }

    private void Stop(AgentLimit limit, string reason)
    {
        MarkStopped(limit, reason);
        throw new AgentLimitExceededException(limit, reason);
    }

    private void MarkStopped(AgentLimit limit, string reason)
    {
        StoppedBy = limit;
        StopReason = reason;
        LogStopped(_logger, RunId, UserId, limit, reason);
    }

    // Local models tokenize differently and some providers report no usage, so the budget counts
    // with chapter 1's tokenizer when it has to. The estimate includes the tool schemas, which are
    // part of every request and easy to forget.
    private int EstimateInputTokens(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var text = string.Join("\n", messages.Select(m => m.Text));
        var tools = options?.Tools is { Count: > 0 } list
            ? string.Join("\n", list.OfType<AIFunctionDeclaration>().Select(t => t.Name + t.Description + t.JsonSchema.GetRawText()))
            : string.Empty;
        var results = string.Join("\n", messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()));
        return _tokens.Count(text) + _tokens.Count(tools) + _tokens.Count(results) + _tokens.Count(options?.Instructions ?? string.Empty);
    }

    private int EstimateOutputTokens(ChatResponse response) =>
        _tokens.Count(response.Text)
        + response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Sum(c => _tokens.Count(c.Name) + _tokens.Count(System.Text.Json.JsonSerializer.Serialize(c.Arguments, AIJsonUtilities.DefaultOptions)));

    // Steps are logged with the run, the user, and the counts: who did what, and what it cost.
    // Never the prompt, the arguments, or the answer, which can hold PHI (chapter 13 adds the audit log).
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run {RunId} for {UserId}: step {Step} model call, {InputTokens} in, {OutputTokens} out, ${Cost}.")]
    private static partial void LogModelStep(ILogger logger, string runId, string userId, int step, int inputTokens, int outputTokens, decimal cost);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run {RunId} for {UserId}: step {Step} tool call {Tool}.")]
    private static partial void LogToolStep(ILogger logger, string runId, string userId, int step, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent run {RunId} for {UserId} stopped ({Limit}): {Reason}")]
    private static partial void LogStopped(ILogger logger, string runId, string userId, AgentLimit limit, string reason);
}
