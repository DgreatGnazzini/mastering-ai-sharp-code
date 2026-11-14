using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PriorAuthBot.Core;
using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;

namespace PriorAuthBot.Eval;

/// <summary>
/// PriorAuthBot's services, built from configuration exactly as the API builds them, over the
/// sample documents. The only additions are an optional on-disk cache and a recorder around the
/// chat client, so the leak check sees the exact prompt the model was sent.
/// </summary>
internal sealed class EvalHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private EvalHost(ServiceProvider services, PromptRecorder recorder)
    {
        _services = services;
        Prompts = recorder;
    }

    public IServiceProvider Services => _services;

    /// <summary>Every request sent to the main chat client since the last <see cref="PromptRecorder.Clear"/>.</summary>
    public PromptRecorder Prompts { get; }

    public static EvalHost Create(IDictionary<string, string?> settings, IDistributedCache? cache = null, IChatClient? chatClient = null, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddPriorAuthBot(configuration);
        if (cache is not null)
        {
            services.AddSingleton(cache);
        }

        if (chatClient is not null)
        {
            services.AddSingleton(chatClient);
        }

        configure?.Invoke(services);
        var recorder = new PromptRecorder();
        DecorateChatClient(services, recorder);
        return new EvalHost(services.BuildServiceProvider(), recorder);
    }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public Task<IngestionReport> IngestSamplesAsync(CancellationToken cancellationToken = default) =>
        Get<IngestionPipeline>().SyncAsync(MarkdownDocumentLoader.LoadDirectoryAsync(EvalPaths.SampleDocuments, cancellationToken), cancellationToken);

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    /// <summary>Wraps the last <see cref="IChatClient"/> registration, whatever built it, in the recorder.</summary>
    private static void DecorateChatClient(IServiceCollection services, PromptRecorder recorder)
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(IChatClient) && !d.IsKeyedService);
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(typeof(IChatClient), sp =>
        {
            var inner = descriptor.ImplementationInstance as IChatClient
                ?? descriptor.ImplementationFactory?.Invoke(sp) as IChatClient
                ?? (IChatClient)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
            return recorder.Wrap(inner);
        }, descriptor.Lifetime));
    }
}

/// <summary>Records the text of every request, then passes it on unchanged.</summary>
internal sealed class PromptRecorder
{
    private readonly List<string> _prompts = [];

    public IReadOnlyList<string> Prompts => _prompts;

    public void Clear() => _prompts.Clear();

    public IChatClient Wrap(IChatClient inner) => new RecordingClient(inner, this);

    private sealed class RecordingClient(IChatClient inner, PromptRecorder recorder) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            recorder._prompts.Add(string.Join("\n", list.Select(m => m.Text)));
            return base.GetResponseAsync(list, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            recorder._prompts.Add(string.Join("\n", list.Select(m => m.Text)));
            return base.GetStreamingResponseAsync(list, options, cancellationToken);
        }
    }
}
