using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Observability;

namespace PriorAuthBot.Core.Usage;

/// <summary>
/// Chapter 13's metering middleware. Before a model call, it checks the user's daily token budget
/// and refuses the call when it is spent. After the call, it charges the tokens the provider
/// reported (or chapter 1's estimate when a provider reports none) to the user, the request's audit
/// record, and the cost metrics, priced from <see cref="UsageOptions.Prices"/>.
/// </summary>
/// <remarks>
/// It sits in every chat client's pipeline, so a budget covers <c>/ask</c>, <c>/assist</c>, the
/// agent, and the classifier alike, and a tool loop's five round trips are five charges. It reads
/// the user from <see cref="AiRequestContext.Current"/>; a call outside a request, such as OCR during
/// ingestion, is metered under <c>background</c> and has no budget.
/// </remarks>
public sealed partial class MeteringChatClient(
    IChatClient innerClient,
    ITokenLedger ledger,
    IOptionsMonitor<UsageOptions> options,
    TokenCounter tokens,
    ILogger<MeteringChatClient> logger) : DelegatingChatClient(innerClient)
{
    public const string BackgroundUser = "background";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var estimate = CheckBudget(list, options);
        var response = await base.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        Charge(response.Usage, estimate, response.Text, response.ModelId ?? options?.ModelId);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var estimate = CheckBudget(list, options);
        UsageDetails? usage = null;
        string? model = null;
        var text = new System.Text.StringBuilder();
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
        {
            model ??= update.ModelId;
            text.Append(update.Text);
            foreach (var content in update.Contents.OfType<UsageContent>())
            {
                usage ??= new UsageDetails();
                usage.Add(content.Details);
            }

            yield return update;
        }

        Charge(usage, estimate, text.ToString(), model ?? options?.ModelId);
    }

    private long CheckBudget(IList<ChatMessage> messages, ChatOptions? chatOptions)
    {
        var estimate = (long)tokens.Count(string.Join("\n", messages.Select(m => m.Text))) + tokens.Count(chatOptions?.Instructions ?? string.Empty);
        var request = AiRequestContext.Current;
        var settings = options.CurrentValue;
        if (request is null || !settings.QuotasEnabled)
        {
            return estimate;
        }

        var limit = settings.DailyLimitFor(request.Groups);
        var used = ledger.ForUser(request.UserId).TotalTokens;

        // Refuse when the budget is spent, or when this call's input alone would go over it, so one
        // enormous prompt cannot overdraw the day.
        if (used >= limit || used + estimate > limit)
        {
            PriorAuthBotTelemetry.QuotaRejections.Add(1, new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.Operation, request.Operation));
            LogQuotaExceeded(logger, request.UserId, used, limit);
            throw new TokenQuotaExceededException(request.UserId, used, limit, ledger.ResetsAt);
        }

        return estimate;
    }

    private void Charge(UsageDetails? usage, long estimatedInput, string outputText, string? model)
    {
        var input = usage?.InputTokenCount ?? estimatedInput;
        var output = usage?.OutputTokenCount ?? tokens.Count(outputText);
        model ??= ((IChatClient)this).GetService<ChatClientMetadata>()?.DefaultModelId ?? "unknown";
        var cost = options.CurrentValue.PriceFor(model).Cost(input, output);

        var request = AiRequestContext.Current;
        var operation = request?.Operation ?? BackgroundUser;
        var modelTag = new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.Model, model);
        var operationTag = new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.Operation, operation);
        PriorAuthBotTelemetry.Tokens.Add(input, modelTag, operationTag, new(PriorAuthBotTelemetry.Tags.TokenType, "input"));
        PriorAuthBotTelemetry.Tokens.Add(output, modelTag, operationTag, new(PriorAuthBotTelemetry.Tags.TokenType, "output"));
        PriorAuthBotTelemetry.Cost.Add((double)cost, modelTag, operationTag);
        Activity.Current?.SetTag("priorauthbot.cost_usd", (double)cost);

        request?.RecordUsage(input, output, cost);
        foreach (var threshold in ledger.Charge(request?.UserId ?? BackgroundUser, input, output, cost))
        {
            var today = ledger.Today();
            PriorAuthBotTelemetry.SpendAlerts.Add(1, new KeyValuePair<string, object?>("priorauthbot.spend.threshold", threshold));
            LogSpendAlert(logger, threshold, today.Cost, options.CurrentValue.DailySpendAlertUsd);
        }
    }

    // The user ID is logged here on purpose: a budget refusal is an access decision, and the audit
    // log records the same request under the same ID. No prompt text, ever.
    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Refused a model call for {UserId}: {Used} of {Limit} daily tokens used.")]
    private static partial void LogQuotaExceeded(ILogger logger, string userId, long used, long limit);

    // Alert rules match this event ID (chapter 14 turns it into an Azure Monitor alert).
    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "Spend alert: today's model spend crossed {Threshold:P0} of the daily alert level (${Spent:0.00} of ${Level:0.00}).")]
    private static partial void LogSpendAlert(ILogger logger, double threshold, decimal spent, decimal level);
}
