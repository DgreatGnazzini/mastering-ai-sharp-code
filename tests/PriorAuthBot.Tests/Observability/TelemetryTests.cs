using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PriorAuthBot.Api;
using PriorAuthBot.Core.Observability;
using Xunit;

namespace PriorAuthBot.Tests.Observability;

/// <summary>
/// Chapter 13: traces show retrieval, the model call, and the audit ID, and never the question. The
/// listener sees every span the API makes, exactly what an exporter would send.
/// </summary>
public sealed class TelemetryTests : IClassFixture<ObservabilityFactory>, IDisposable
{
    private readonly ObservabilityFactory _factory;
    private readonly ConcurrentBag<Activity> _spans = [];
    private readonly ActivityListener _listener;

    public TelemetryTests(ObservabilityFactory factory)
    {
        _factory = factory;
        _listener = Listen(_spans);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnswerIsTracedFromRequestToModelCall()
    {
        var response = await _factory.As("nurse").PostAsJsonAsync("/ask", new AskRequest("When must a positive sepsis screen be escalated to a physician?"), Ct);
        response.EnsureSuccessStatusCode();
        var auditId = Assert.Single(response.Headers.GetValues("X-Audit-Id"));

        var ask = Assert.Single(_spans, s => s.OperationName == "priorauthbot.ask" && (string?)s.GetTagItem(PriorAuthBotTelemetry.Tags.AuditId) == auditId);
        var trace = _spans.Where(s => s.TraceId == ask.TraceId).ToList();

        Assert.Contains(trace, s => s.OperationName == "priorauthbot.retrieve" && (int?)s.GetTagItem(PriorAuthBotTelemetry.Tags.DocumentCount) > 0);
        var chat = Assert.Single(trace, s => s.Source.Name == PriorAuthBotTelemetry.AiSourceName && s.OperationName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Equal("chat", chat.GetTagItem("gen_ai.operation.name"));
        Assert.Contains(trace, s => s.Source.Name == PriorAuthBotTelemetry.AiSourceName && s.OperationName.StartsWith("embeddings", StringComparison.Ordinal));
        Assert.Equal(false, ask.GetTagItem(PriorAuthBotTelemetry.Tags.Declined));
    }

    [Fact]
    public async Task NoSpanCarriesTheQuestionOrTheUser()
    {
        const string question = "Does member DD-7714402 (marker quokka-5521) need prior authorization for a lumbar MRI?";
        var response = await _factory.As("nurse").PostAsJsonAsync("/ask", new AskRequest(question), Ct);
        response.EnsureSuccessStatusCode();

        Assert.NotEmpty(_spans);
        foreach (var text in AllText(_spans))
        {
            Assert.DoesNotContain("quokka-5521", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DD-7714402", text, StringComparison.Ordinal);
            Assert.DoesNotContain("demo-nurse", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ContentCaptureIsASettingAndOnlyForDevelopment()
    {
        // The switch works: turned on in Development, the same question shows up on the gen_ai spans.
        using var capturing = new CapturingFactory();
        await capturing.InitializeAsync();
        var response = await capturing.As("nurse").PostAsJsonAsync("/ask", new AskRequest("Can I work remotely? (marker wombat-9083)"), Ct);
        response.EnsureSuccessStatusCode();

        Assert.Contains(AllText(_spans), t => t.Contains("wombat-9083", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Telemetry:CaptureMessageContent", "true")]
    [InlineData("Logging:LogLevel:Microsoft.Extensions.AI", "Trace")]
    [InlineData("Audit:Store", "InMemory")]
    public void ProductionRefusesSettingsThatWouldLeakOrLoseAuditData(string key, string value)
    {
        using var production = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("AI:Provider", "Offline");
            b.UseSetting("VectorStore:Provider", "InMemory");
            b.UseSetting(key, value);
        });

        var ex = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains(key.Split(':')[0], ex.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _listener.Dispose();

    private static IEnumerable<string> AllText(IEnumerable<Activity> spans) =>
        spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}")))
            .Append(s.DisplayName));

    private static ActivityListener Listen(ConcurrentBag<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("PriorAuthBot", StringComparison.Ordinal) || source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class CapturingFactory : ObservabilityFactory
    {
        protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
        {
            ["Telemetry:CaptureMessageContent"] = "true",
        };
    }
}
