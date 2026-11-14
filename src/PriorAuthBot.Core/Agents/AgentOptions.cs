namespace PriorAuthBot.Core.Agents;

/// <summary>
/// The limits every PriorAuthBot agent run works inside (chapter 11), from the <c>Agents</c> section
/// of configuration. They are per run: one question to the policy agent, or one stretch of the
/// exception workflow between two human answers.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    /// <summary>
    /// The kill switch. Read on every model call and every tool call, through IOptionsMonitor, so
    /// turning it off in configuration stops runs that are already going, at their next step.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most model calls one run may make: the step limit.</summary>
    public int MaxModelCalls { get; set; } = 6;

    /// <summary>The most tool calls one run may make.</summary>
    public int MaxToolCalls { get; set; } = 4;

    /// <summary>The spend cap for one run, in US dollars, priced with <see cref="InputPricePerMillion"/> and <see cref="OutputPricePerMillion"/>.</summary>
    public decimal MaxCostUsd { get; set; } = 0.01m;

    /// <summary>
    /// The price used for the spend cap, per million input tokens. Every provider is priced as if it
    /// were the hosted model (gpt-4.1-mini list prices, November 2026), so the cap means the same thing
    /// on a laptop running Ollama as it does in production.
    /// </summary>
    public decimal InputPricePerMillion { get; set; } = 0.40m;

    public decimal OutputPricePerMillion { get; set; } = 1.60m;

    /// <summary>The wall-clock limit for one run.</summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The most turns one conversation with the policy agent may have before the user starts a new one.</summary>
    public int MaxTurnsPerConversation { get; set; } = 12;

    /// <summary>How long a conversation is kept after its last turn.</summary>
    public TimeSpan ConversationLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>How long an exception request may wait for the requester or the manager before it expires.</summary>
    public TimeSpan ExceptionRequestLifetime { get; set; } = TimeSpan.FromDays(14);
}
