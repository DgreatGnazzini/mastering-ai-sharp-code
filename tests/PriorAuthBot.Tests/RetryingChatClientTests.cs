using System.Net;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;
using static PriorAuthBot.Tests.Infrastructure.ScriptedChatClient;

namespace PriorAuthBot.Tests;

public class RetryingChatClientTests
{
    private static readonly ResilienceOptions Fast = new() { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(50) };

    [Fact]
    public async Task RetriesRateLimitsThenSucceeds()
    {
        var model = new ScriptedChatClient(Fail(FakePipelineResponse.Exception(429)), Fail(FakePipelineResponse.Exception(503)), Reply("ok"));

        var response = await new RetryingChatClient(model, Fast).GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var model = new ScriptedChatClient(Fail(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests)));

        await Assert.ThrowsAsync<HttpRequestException>(() => new RetryingChatClient(model, Fast).GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Fast.MaxAttempts, model.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task DoesNotRetryClientErrors(int status)
    {
        var model = new ScriptedChatClient(Fail(FakePipelineResponse.Exception(status)));

        await Assert.ThrowsAnyAsync<Exception>(() => new RetryingChatClient(model, Fast).GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task WaitsForRetryAfterButNoLongerThanMaxDelay()
    {
        var model = new ScriptedChatClient(Fail(FakePipelineResponse.Exception(429, "", ("Retry-After", "3600"))), Reply("ok"));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await new RetryingChatClient(model, Fast).GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        // Timers can fire a millisecond or two early, so allow a little slack below MaxDelay.
        Assert.InRange(watch.Elapsed, Fast.MaxDelay * 0.8, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StreamingRetriesBeforeTheFirstUpdate()
    {
        var model = new ScriptedChatClient(Fail(FakePipelineResponse.Exception(429)), Reply("three short words"));

        var text = string.Concat(await new RetryingChatClient(model, Fast).GetStreamingResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken).Select(u => u.Text).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("three short words", text);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task StreamingDoesNotRetryAfterTheFirstUpdate()
    {
        var model = new ScriptedChatClient(FailAfterFirstWord("three short words", FakePipelineResponse.Exception(503)), Reply("never"));
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var update in new RetryingChatClient(model, Fast).GetStreamingResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken))
            {
                seen.Add(update.Text);
            }
        });

        Assert.Equal(["three"], seen);
        Assert.Equal(1, model.Calls);
    }
}

public class ChatFailureTests
{
    public static TheoryData<Exception, ChatFailureKind> Cases => new()
    {
        { FakePipelineResponse.Exception(429), ChatFailureKind.RateLimited },
        { FakePipelineResponse.Exception(500), ChatFailureKind.Unavailable },
        { FakePipelineResponse.Exception(503), ChatFailureKind.Unavailable },
        { FakePipelineResponse.Exception(408), ChatFailureKind.Timeout },
        { FakePipelineResponse.Exception(400, """{"error":{"code":"content_filter","message":"The response was filtered"}}"""), ChatFailureKind.ContentFiltered },
        { FakePipelineResponse.Exception(400, """{"error":{"code":"invalid_request"}}"""), ChatFailureKind.Unknown },
        { FakePipelineResponse.Exception(401), ChatFailureKind.Unknown },
        { new HttpRequestException("refused"), ChatFailureKind.Unavailable },
        { new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests), ChatFailureKind.RateLimited },
        { new HttpRequestException("down", null, HttpStatusCode.BadGateway), ChatFailureKind.Unavailable },
        { new TimeoutException(), ChatFailureKind.Timeout },
        { new InvalidOperationException(), ChatFailureKind.Unknown },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classifies(Exception exception, ChatFailureKind expected) =>
        Assert.Equal(expected, ChatFailure.Classify(exception));

    [Fact]
    public void ReadsRetryAfter()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), ChatFailure.RetryAfter(FakePipelineResponse.Exception(429, "", ("Retry-After", "7"))));
        Assert.Equal(TimeSpan.FromMilliseconds(250), ChatFailure.RetryAfter(FakePipelineResponse.Exception(429, "", ("retry-after-ms", "250"))));
        Assert.Null(ChatFailure.RetryAfter(FakePipelineResponse.Exception(429)));
    }
}
