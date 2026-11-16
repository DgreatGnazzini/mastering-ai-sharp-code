using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PriorAuthBot.Core.Agents;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// PriorAuthBot's services for the agent tests (chapter 11): chapter 10's tool host, with a scripted
/// model as the plain chat client the agents are built on, a clock the test controls, and limits
/// the test can change.
/// </summary>
internal static class AgentTestHost
{
    public static readonly Core.Retrieval.CallerIdentity Manager = new("demo-manager", "Grace Lindqvist, RN, nurse manager", ["AllStaff", "Clinical"]);
    public static readonly Core.Retrieval.CallerIdentity Staff = new("demo-staff", "Sam Ortiz", ["AllStaff"]);
    public static readonly Core.Retrieval.CallerIdentity Compliance = new("demo-compliance", "Dana Whitfield", ["AllStaff", "Compliance"]);
    public static readonly Core.Retrieval.CallerIdentity Hr = new("demo-hr", "Ingrid Nelson", ["AllStaff", "HR"]);

    public static Task<RagTestHost> CreateAsync(MockHrFactory hr, IChatClient model, ManualTimeProvider? clock = null, Action<AgentOptions>? limits = null, Action<IServiceCollection>? configure = null) =>
        ToolTestHost.CreateAsync(hr, model, services =>
        {
            services.AddChatClient(model);
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock ?? new ManualTimeProvider()));
            if (limits is not null)
            {
                services.PostConfigure(limits);
            }

            configure?.Invoke(services);
        });
}

/// <summary>A model that reports the token usage a test gives it, to drive the spend cap.</summary>
internal sealed class UsageReportingModel(int inputTokens, int outputTokens, Func<IReadOnlyList<ChatMessage>, ChatMessage> reply) : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ChatResponse(reply(messages.ToList())) { Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens } });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
