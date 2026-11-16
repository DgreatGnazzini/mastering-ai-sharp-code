using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PriorAuthBot.Core.Observability;

/// <summary>
/// PriorAuthBot's own spans and metrics (chapter 13), next to the <c>gen_ai.*</c> ones that
/// Microsoft.Extensions.AI's OpenTelemetry middleware writes for every model call. Everything here
/// uses <see cref="System.Diagnostics"/> only, so Core has no OpenTelemetry dependency: the API
/// decides where the data goes (the Aspire dashboard locally, Application Insights in chapter 14).
/// </summary>
/// <remarks>
/// The rule for every attribute and tag: identifiers, counts, and outcomes, never text. No question,
/// answer, chunk, or tool argument is ever written here, and no user ID either. A span carries the
/// audit record's ID instead, so an auditor can go from a trace to who asked, and nobody with
/// access to traces can go the other way.
/// </remarks>
public static class PriorAuthBotTelemetry
{
    /// <summary>The source and meter name for PriorAuthBot's own telemetry.</summary>
    public const string SourceName = "PriorAuthBot";

    /// <summary>The source name given to Microsoft.Extensions.AI's OpenTelemetry middleware.</summary>
    public const string AiSourceName = "PriorAuthBot.AI";

    public static readonly ActivitySource Source = new(SourceName, PriorAuthBotInfo.CurrentChapter);

    public static readonly Meter Meter = new(SourceName, PriorAuthBotInfo.CurrentChapter);

    /// <summary>Tokens the model used, by direction, model, and operation. The billing view of every call.</summary>
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "priorauthbot.ai.tokens", "{token}", "Tokens used by model calls, by direction (input or output), model, and operation.");

    /// <summary>What those tokens cost at the configured prices, in US dollars.</summary>
    public static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "priorauthbot.ai.cost", "USD", "Estimated cost of model calls at the configured prices.");

    public static readonly Counter<long> QuotaRejections = Meter.CreateCounter<long>(
        "priorauthbot.quota.rejections", "{request}", "Model calls refused because the user's daily token budget was spent.");

    public static readonly Counter<long> SpendAlerts = Meter.CreateCounter<long>(
        "priorauthbot.spend.alerts", "{alert}", "Daily spend crossed an alert threshold.");

    public static readonly Counter<long> AnswerCacheLookups = Meter.CreateCounter<long>(
        "priorauthbot.answer_cache.lookups", "{lookup}", "Answer cache lookups, by result (hit or miss).");

    public static readonly Counter<long> AuditRecords = Meter.CreateCounter<long>(
        "priorauthbot.audit.records", "{record}", "Audit records written, by result (written or failed).");

    public static readonly Counter<long> Feedback = Meter.CreateCounter<long>(
        "priorauthbot.feedback", "{rating}", "User ratings of answers, by rating (helpful or not).");

    /// <summary>Attribute names. <c>gen_ai.*</c> ones follow the OpenTelemetry GenAI semantic conventions.</summary>
    public static class Tags
    {
        public const string Operation = "priorauthbot.operation";
        public const string AuditId = "priorauthbot.audit.id";
        public const string Model = "gen_ai.request.model";
        public const string TokenType = "gen_ai.token.type";
        public const string RetrievalMode = "priorauthbot.retrieval.mode";
        public const string ChunkCount = "priorauthbot.retrieval.chunks";
        public const string DocumentCount = "priorauthbot.retrieval.documents";
        public const string PhiDocumentCount = "priorauthbot.retrieval.phi_documents";
        public const string Declined = "priorauthbot.answer.declined";
        public const string CacheResult = "priorauthbot.cache.result";
        public const string Result = "priorauthbot.result";
    }
}
