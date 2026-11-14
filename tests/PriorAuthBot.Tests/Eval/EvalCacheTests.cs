using Microsoft.Extensions.AI;
using PriorAuthBot.Eval;
using Xunit;

namespace PriorAuthBot.Tests.Eval;

/// <summary>
/// A cached evaluation run (chapter 9): MEAI's caching client over a cache on disk. The second run
/// of an unchanged question, even from a new process, never reaches the model.
/// </summary>
public sealed class EvalCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "priorauthbot-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ASecondRunAnswersFromTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new CountingChatClient();
        ChatMessage[] question = [new(ChatRole.User, "How many hours of PTO can I carry over?")];

        using (var firstRun = new DistributedCachingChatClient(model, new FileDistributedCache(_directory)))
        {
            await firstRun.GetResponseAsync(question, new ChatOptions { Temperature = 0 }, ct);
        }

        var cache = new FileDistributedCache(_directory);
        using var secondRun = new DistributedCachingChatClient(model, cache);
        var reply = await secondRun.GetResponseAsync(question, new ChatOptions { Temperature = 0 }, ct);

        Assert.Equal(1, model.Calls);
        Assert.Equal("answer 1", reply.Text);
        Assert.Equal(1, cache.Hits);

        // A changed prompt is a new key, so it reaches the model.
        await secondRun.GetResponseAsync([new(ChatRole.User, "How many hours of PTO can I carry over into 2027?")], new ChatOptions { Temperature = 0 }, ct);
        Assert.Equal(2, model.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"answer {++Calls}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
