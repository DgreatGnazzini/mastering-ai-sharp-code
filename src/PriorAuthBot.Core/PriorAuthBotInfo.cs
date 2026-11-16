namespace PriorAuthBot.Core;

/// <summary>
/// PriorAuthBot is the internal policy and knowledge assistant for Deductible Dynamics, a fictional
/// regional health plan. Each chapter of the book adds one layer to it.
/// </summary>
public sealed record PriorAuthBotInfo(string Name, string Organization, string Chapter)
{
    /// <summary>The chapter tag this code corresponds to ("ch00" is the starting skeleton).</summary>
    public const string CurrentChapter = "ch07";

    public static PriorAuthBotInfo Current { get; } = new("PriorAuthBot", "Deductible Dynamics", CurrentChapter);
}
