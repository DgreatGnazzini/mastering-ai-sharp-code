namespace PriorAuthBot.Core.Observability;

/// <summary>What PriorAuthBot's telemetry may contain (chapter 13).</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Put prompts, answers, and tool arguments on the <c>gen_ai</c> spans. Off by default, and the
    /// API refuses to start with it on outside Development: traces go to systems with far wider access
    /// than the audit log, and a prompt can hold PHI. Turn it on to debug a prompt on your own machine.
    /// </summary>
    public bool CaptureMessageContent { get; set; }
}
