using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core.Observability;
using PriorAuthBot.Core.Privacy;

namespace PriorAuthBot.Core.AI;

public enum ModelRoute
{
    Hosted,
    Local,
}

/// <summary>Where a request went, and the first reason it went local.</summary>
public sealed record RouteDecision(ModelRoute Route, string Reason)
{
    public static readonly RouteDecision Hosted = new(ModelRoute.Hosted, "no PHI found");
}

/// <summary>
/// Chapter 15: decides whether a request carries PHI. Any one of these sends it to the local model:
/// <list type="bullet">
/// <item>an image or file (a scan cannot be checked with a pattern, so it is treated as PHI);</item>
/// <item>the caller flagged it, with <see cref="PhiKey"/> in <see cref="ChatOptions.AdditionalProperties"/>.
/// The RAG service does this when a source it retrieved is marked <c>phi: true</c>;</item>
/// <item>an identifier with a known shape, such as a member ID or an MRN, anywhere in the text,
/// whether the user typed it or a document brought it in.</item>
/// </list>
/// It errs toward local: a false positive costs a slower answer, a false negative sends PHI to a
/// provider. A hosted provider without a BAA still has chapter 4's PHI guard behind this, so a
/// question the rules miss is redacted, not sent as it is.
/// </summary>
public static class PhiRouting
{
    /// <summary>The flag a caller sets in <see cref="ChatOptions.AdditionalProperties"/> for a request it knows carries PHI.</summary>
    public const string PhiKey = "priorauthbot.phi";

    public static RouteDecision Decide(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (options?.AdditionalProperties?.TryGetValue(PhiKey, out var flag) == true && flag is true or "true")
        {
            return new RouteDecision(ModelRoute.Local, "flagged by caller");
        }

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case DataContent or UriContent:
                        return new RouteDecision(ModelRoute.Local, "image or file");
                    case TextContent text when PhiRedactor.Redact(text.Text).Counts is { Count: > 0 } counts:
                        return new RouteDecision(ModelRoute.Local, "identifier: " + counts.Keys.Order(StringComparer.Ordinal).First());
                }
            }
        }

        return RouteDecision.Hosted;
    }

    /// <summary>Marks <paramref name="options"/> as carrying PHI, and returns it.</summary>
    public static ChatOptions Flag(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        (options.AdditionalProperties ??= [])[PhiKey] = true;
        return options;
    }
}

/// <summary>
/// Chapter 15's hybrid routing: one <see cref="IChatClient"/> in front of two complete pipelines,
/// one on the hosted model and one on a local model. <see cref="PhiRouting.Decide"/> picks. Each
/// branch has its own guardrails, telemetry, metering, and retries, so routing changes where a
/// request goes and nothing else about it.
/// </summary>
/// <remarks>
/// It fails closed. When the local model is down, a PHI request fails; it is never retried on the
/// hosted model, because "the local model was slow" is not a reason the BAA covers.
/// </remarks>
public sealed partial class PhiRoutingChatClient(IChatClient hosted, IChatClient local, ILogger<PhiRoutingChatClient> logger) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        return Pick(list, options).GetResponseAsync(list, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        await foreach (var update in Pick(list, options).GetStreamingResponseAsync(list, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private IChatClient Pick(IList<ChatMessage> messages, ChatOptions? options)
    {
        var decision = PhiRouting.Decide(messages, options);
        var route = decision.Route == ModelRoute.Local ? "local" : "hosted";
        Activity.Current?.SetTag(PriorAuthBotTelemetry.Tags.Route, route);
        PriorAuthBotTelemetry.ModelRoutes.Add(1, new KeyValuePair<string, object?>(PriorAuthBotTelemetry.Tags.Route, route), new("priorauthbot.route.reason", decision.Reason));
        if (decision.Route == ModelRoute.Local)
        {
            LogLocal(logger, decision.Reason);
            return local;
        }

        return hosted;
    }

    /// <summary>The hosted branch's metadata: it is the default, and what most requests use.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : hosted.GetService(serviceType, serviceKey);
    }

    /// <summary>Nothing to do: both branches are registered services, and the container disposes them.</summary>
    public void Dispose()
    {
    }

    // The reason is a category ("identifier: MEMBER_ID"), never the identifier itself.
    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Routed a model call to the local model: {Reason}.")]
    private static partial void LogLocal(ILogger logger, string reason);
}
