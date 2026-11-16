using PriorAuthBot.Core.Documents;
using PriorAuthBot.Core.Ingestion;

namespace PriorAuthBot.Api.Ingestion;

public sealed class IngestionWorkerOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Folder of policies, relative to the content root unless absolute. Markdown, PDF, Word, and
    /// scanned files are all read (chapter 4); point it at samples/files to try the other formats.
    /// </summary>
    public string DocumentsPath { get; set; } = "../../samples/documents";

    /// <summary>
    /// How often to resync. This interval is also the worst-case delay before a permission change
    /// or delete reaches the index, which is the number your security reviewer will ask for.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Runs the ingestion pipeline at startup and then on a timer (chapter 7).</summary>
public sealed partial class IngestionWorker(
    IngestionPipeline pipeline,
    DocumentLoader loader,
    Microsoft.Extensions.Options.IOptions<IngestionWorkerOptions> options,
    IHostEnvironment environment,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes after the first sync, so tests and readiness checks can wait for it.</summary>
    public Task FirstRunCompleted => _firstRun.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.DocumentsPath));
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await pipeline.SyncAsync(loader.LoadDirectoryAsync(path, stoppingToken), stoppingToken);
                _firstRun.TrySetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the old index rather than crash; the next tick retries.
                LogFailed(logger, ex, path);
                _firstRun.TrySetException(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion from {Path} failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string path);
}
