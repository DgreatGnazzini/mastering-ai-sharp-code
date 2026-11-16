using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PriorAuthBot.Api;
using PriorAuthBot.Api.Observability;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Audit;
using PriorAuthBot.Core.Usage;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Observability;

/// <summary>Chapter 13: per-user daily token budgets, cost from the provider's usage, and spend alerts.</summary>
public sealed class TokenBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProviderUsageIsChargedToTheUserAndTheRequest()
    {
        var (client, ledger) = Metered(new UsageOptions
        {
            Prices = { ["gpt-4.1-mini"] = new TokenPrice { InputPerMillion = 0.40m, OutputPerMillion = 1.60m } },
        });

        using var scope = AiRequestContext.Begin("POST /ask", "demo-nurse", "Nina");
        await client.GetResponseAsync("When must a sepsis screen be escalated?", cancellationToken: Ct);

        var usage = ledger.ForUser("demo-nurse");
        Assert.Equal(1_000, usage.InputTokens);
        Assert.Equal(100, usage.OutputTokens);
        Assert.Equal(0.00056m, usage.Cost); // 1,000 x $0.40/M + 100 x $1.60/M
        Assert.Equal(1_100, scope.Context.InputTokens + scope.Context.OutputTokens);
        Assert.Equal(1, scope.Context.ModelCalls);
    }

    [Fact]
    public async Task ASpentBudgetRefusesTheNextCallBeforeItReachesTheModel()
    {
        var model = new UsageReportingClient(input: 1_000, output: 100);
        var (client, _) = Metered(new UsageOptions { DailyTokensPerUser = 1_000 }, model);

        using var scope = AiRequestContext.Begin("POST /ask", "demo-hr", "Ingrid");
        await client.GetResponseAsync("first", cancellationToken: Ct);
        await Assert.ThrowsAsync<TokenQuotaExceededException>(() => client.GetResponseAsync("second", cancellationToken: Ct));

        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task ABudgetIsPerUser()
    {
        var (client, _) = Metered(new UsageOptions { DailyTokensPerUser = 1_000 });

        using (AiRequestContext.Begin("POST /ask", "demo-hr", "Ingrid"))
        {
            await client.GetResponseAsync("first", cancellationToken: Ct);
            await Assert.ThrowsAsync<TokenQuotaExceededException>(() => client.GetResponseAsync("second", cancellationToken: Ct));
        }

        using (AiRequestContext.Begin("POST /ask", "demo-claims", "Marcus"))
        {
            await client.GetResponseAsync("first", cancellationToken: Ct);
        }
    }

    [Fact]
    public void AGroupCanHaveALargerBudget()
    {
        var options = new UsageOptions { DailyTokensPerUser = 200_000, GroupDailyTokens = { ["Clinical"] = 400_000 } };

        Assert.Equal(400_000, options.DailyLimitFor(["AllStaff", "Clinical"]));
        Assert.Equal(200_000, options.DailyLimitFor(["AllStaff"]));
    }

    [Fact]
    public async Task BudgetsResetAtMidnightUtc()
    {
        var time = new ManualTimeProvider { Now = new DateTimeOffset(2026, 11, 15, 23, 59, 0, TimeSpan.Zero) };
        var (client, ledger) = Metered(new UsageOptions { DailyTokensPerUser = 1_000 }, time: time);

        using var scope = AiRequestContext.Begin("POST /ask", "demo-hr", "Ingrid");
        await client.GetResponseAsync("first", cancellationToken: Ct);
        var refused = await Assert.ThrowsAsync<TokenQuotaExceededException>(() => client.GetResponseAsync("second", cancellationToken: Ct));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), refused.ResetsAt);

        time.Now += TimeSpan.FromMinutes(2);
        await client.GetResponseAsync("third", cancellationToken: Ct);
        Assert.Equal(1_100, ledger.ForUser("demo-hr").TotalTokens);
    }

    [Fact]
    public async Task CallsOutsideARequestAreMeteredButHaveNoBudget()
    {
        var (client, ledger) = Metered(new UsageOptions { DailyTokensPerUser = 10 });

        await client.GetResponseAsync("OCR this page", cancellationToken: Ct);
        await client.GetResponseAsync("OCR the next page", cancellationToken: Ct);

        Assert.Equal(2, ledger.ForUser(MeteringChatClient.BackgroundUser).ModelCalls);
    }

    [Fact]
    public async Task WithNoUsageFromTheProviderTheTokensAreEstimated()
    {
        var (client, ledger) = Metered(new UsageOptions(), new UsageReportingClient(input: null, output: null));

        using var scope = AiRequestContext.Begin("POST /chat", "demo-nurse", "Nina");
        await client.GetResponseAsync("Count these words please, all of them.", cancellationToken: Ct);

        Assert.InRange(ledger.ForUser("demo-nurse").InputTokens, 5, 20);
        Assert.True(ledger.ForUser("demo-nurse").OutputTokens > 0);
    }

    [Fact]
    public void SpendAlertsFireOncePerThresholdPerDay()
    {
        var ledger = new InMemoryTokenLedger(Monitor(new UsageOptions { DailySpendAlertUsd = 1m, AlertThresholds = [0.5, 1.0] }));

        Assert.Empty(ledger.Charge("a", 0, 0, 0.30m));
        Assert.Equal([0.5], ledger.Charge("b", 0, 0, 0.30m));
        Assert.Empty(ledger.Charge("c", 0, 0, 0.30m));
        Assert.Equal([1.0], ledger.Charge("a", 0, 0, 0.30m));
        Assert.Empty(ledger.Charge("a", 0, 0, 5m));
        Assert.Equal(3, ledger.Today().Users);
    }

    [Fact]
    public async Task TheApiAnswersASpentBudgetWith429AndRetryAfter()
    {
        await using var factory = new TinyBudgetFactory();
        await factory.InitializeAsync();
        var hr = factory.As("hr");

        var response = await hr.PostAsJsonAsync("/ask", new AskRequest("How many PTO days can I carry over?"), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var auditId = Assert.Single(response.Headers.GetValues("X-Audit-Id"));
        var record = await factory.Services.GetRequiredService<AuditLog>().QueryAsync(new AuditQuery(Id: auditId), Ct).SingleAsync(Ct);
        Assert.Equal(429, record.Status);
        Assert.Equal(nameof(TokenQuotaExceededException), record.Error);

        var usage = await hr.GetFromJsonAsync<UsageResponse>("/usage", Ct);
        Assert.Equal(50, usage!.DailyLimit);
    }

    [Fact]
    public async Task TheChatStreamAnswersASpentBudgetWith429Too()
    {
        await using var factory = new TinyBudgetFactory();
        await factory.InitializeAsync();

        var response = await factory.As("hr").PostAsJsonAsync("/chat", new ChatRequest(string.Join(' ', Enumerable.Repeat("long question", 40))), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private static (IChatClient Client, InMemoryTokenLedger Ledger) Metered(UsageOptions options, IChatClient? model = null, TimeProvider? time = null)
    {
        var ledger = new InMemoryTokenLedger(Monitor(options), time);
        var client = new MeteringChatClient(model ?? new UsageReportingClient(1_000, 100), ledger, Monitor(options), new TokenCounter(), NullLogger<MeteringChatClient>.Instance);
        return (client, ledger);
    }

    private static StaticMonitor Monitor(UsageOptions options) => new(options);

    private sealed class StaticMonitor(UsageOptions value) : IOptionsMonitor<UsageOptions>
    {
        public UsageOptions CurrentValue => value;

        public UsageOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<UsageOptions, string?> listener) => null;
    }

    /// <summary>A model that reports usage the way OpenAI and Ollama do, or none at all.</summary>
    private sealed class UsageReportingClient(long? input, long? output) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "An answer of a few words."))
            {
                ModelId = "gpt-4.1-mini",
                Usage = input is null ? null : new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TinyBudgetFactory : ObservabilityFactory
    {
        protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
        {
            ["Usage:DailyTokensPerUser"] = "50",
        };
    }
}
