using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PriorAuthBot.Api.Auth;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Retrieval;
using PriorAuthBot.Core.Tools;
using DeductibleDynamics.MockHr;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>The mock HR API in the test process, in Development mode, as PriorAuthBot's tools see it.</summary>
public sealed class MockHrFactory : WebApplicationFactory<MockHrProgram>
{
    public const string SigningKey = "development-only-key-for-the-mock-hr-api-never-use-it-anywhere-else";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    /// <summary>The development token provider, configured as the API's Development settings configure it.</summary>
    public static DevelopmentTokenProvider Tokens(TimeProvider? time = null) =>
        new(Options.Create(new DevelopmentTokenOptions { SigningKey = SigningKey }), time ?? TimeProvider.System);
}

/// <summary>
/// PriorAuthBot's services for the tool tests: the sample documents ingested, the mock HR API reached
/// through the development token provider, and the tool chat client replaced by a scripted model
/// under the real FunctionInvokingChatClient.
/// </summary>
internal static class ToolTestHost
{
    public static readonly CallerIdentity Nurse = new("demo-nurse", "Nina Okafor, RN", ["AllStaff", "Clinical"]);
    public static readonly CallerIdentity Contractor = new("demo-contractor", "Casey Lin", ["Contractors"]);
    public static readonly CallerIdentity Claims = new("demo-claims", "Marcus Reyes", ["AllStaff", "Claims"]);

    public static async Task<RagTestHost> CreateAsync(MockHrFactory hr, IChatClient? model = null, Action<IServiceCollection>? configure = null)
    {
        var host = RagTestHost.Create(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDownstreamTokenProvider>(MockHrFactory.Tokens()));
            services.Replace(ServiceDescriptor.Singleton(sp => new HrClient(
                hr.CreateClient(), sp.GetRequiredService<IDownstreamTokenProvider>(), sp.GetRequiredService<IOptions<HrApiOptions>>())));
            if (model is not null)
            {
                services.AddKeyedChatClient(AiServiceCollectionExtensions.ToolsKey, model).UsePriorAuthBotToolInvocation();
            }

            configure?.Invoke(services);
        });
        await host.IngestSamplesAsync();
        return host;
    }
}
