using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

/// <summary>
/// Chapter 2 against a real local model: one streamed reply through PriorAuthBot's full chat
/// pipeline, timed. Writes time to first token and total time. Runs when PRIORAUTHBOT_TEST_OLLAMA is set.
/// </summary>
public sealed class LiveChatTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StreamsFromOllama()
    {
        Assert.SkipWhen(LiveSettings.Ollama is null, "Set PRIORAUTHBOT_TEST_OLLAMA to run against a local model.");
        var ct = TestContext.Current.CancellationToken;
        await using var host = RagTestHost.Create(LiveSettings.OllamaConfiguration());
        var chat = host.Services.GetRequiredService<IChatClient>();
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are PriorAuthBot, the internal assistant for Deductible Dynamics staff. Keep answers short."),
            new(ChatRole.User, "In two sentences, what is prior authorization?"),
        ];

        // Warm up: the first call loads the model into memory, which is not what we want to time.
        await chat.GetResponseAsync("Say OK.", cancellationToken: ct);

        var watch = Stopwatch.StartNew();
        TimeSpan? firstToken = null;
        var updates = 0;
        var text = new System.Text.StringBuilder();
        UsageDetails? usage = null;
        await foreach (var update in chat.GetStreamingResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, ct))
        {
            if (firstToken is null && !string.IsNullOrEmpty(update.Text))
            {
                firstToken = watch.Elapsed;
            }

            updates++;
            text.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details ?? usage;
        }

        var total = watch.Elapsed;
        var report = string.Create(CultureInfo.InvariantCulture,
            $"| Model | Time to first token | Total | Updates | Input tokens | Output tokens |\n|---|---|---|---|---|---|\n| {LiveSettings.ChatModel} | {firstToken?.TotalSeconds:0.00} s | {total.TotalSeconds:0.00} s | {updates} | {usage?.InputTokenCount} | {usage?.OutputTokenCount} |\n\n> {text.ToString().Trim()}\n");
        await File.WriteAllTextAsync(Path.Combine(LiveSettings.ReportDirectory, "chat-streaming-ollama.md"), report, ct);
        output.WriteLine(report);

        Assert.NotNull(firstToken);
        Assert.True(updates > 1, "Expected a streamed reply in more than one update.");
    }
}
