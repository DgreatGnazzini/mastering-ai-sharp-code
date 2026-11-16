using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PriorAuthBot.Eval;

/// <summary>
/// Chapter 14: the smoke test the deploy pipeline runs against a new revision before it gets any
/// traffic, and CI runs against the container image. It needs no account and no token, so it can
/// run anywhere the revision is reachable, and it checks only what must be true of every revision:
///   - it is alive, and ready (database reachable, first ingestion pass done) within the wait;
///   - it is the build the pipeline deployed (the revision in the readiness response);
///   - every API endpoint refuses a request with no token, or with a forged one;
///   - the development-only chat page is not served.
///   dotnet run --project eval/PriorAuthBot.Eval -- --smoke http://localhost:8080 --expect-revision abc123 --wait 600
/// Exit code 1 means a check failed.
/// </summary>
internal static class SmokeTest
{
    // A structurally valid JWT that no tenant issued. A revision that accepts it has lost its token validation.
    private const string ForgedToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJvaWQiOiJmb3JnZWQiLCJyb2xlcyI6WyJQcmlvckF1dGhCb3QuVXNlciJdLCJhdWQiOiJhcGk6Ly9wcmlvcmF1dGhib3QifQ.c2lnbmF0dXJl";

    public static async Task<int> RunAsync(string[] args, HttpMessageHandler? handler = null)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var target = new Uri(Value("--smoke") ?? throw new ArgumentException("--smoke needs the revision's base URL."));
        var expectedRevision = Value("--expect-revision");
        var wait = TimeSpan.FromSeconds(int.Parse(Value("--wait") ?? "300", CultureInfo.InvariantCulture));
        var reportDirectory = Value("--out") ?? Environment.GetEnvironmentVariable("PRIORAUTHBOT_TEST_REPORTS") ?? Path.Combine(Directory.GetCurrentDirectory(), "eval-reports");

        using var http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null) { BaseAddress = target, Timeout = TimeSpan.FromSeconds(30) };
        var checks = new List<(string Check, bool Ok, string Detail)>();
        void Record(string check, bool ok, string detail)
        {
            checks.Add((check, ok, detail));
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {check}: {detail}");
        }

        // 1. Ready within the wait. The time includes the first ingestion pass: every document embedded.
        var clock = Stopwatch.StartNew();
        string? readyBody = null;
        HttpStatusCode? lastStatus = null;
        while (clock.Elapsed < wait)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("health/ready", UriKind.Relative));
                lastStatus = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    readyBody = await response.Content.ReadAsStringAsync();
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        var secondsToReady = clock.Elapsed.TotalSeconds;
        Record("Ready", readyBody is not null, readyBody is not null
            ? string.Create(CultureInfo.InvariantCulture, $"after {secondsToReady:0} s")
            : $"not ready after {wait.TotalSeconds:0} s (last status {lastStatus?.ToString() ?? "no answer"})");

        using (var live = await Send(http, HttpMethod.Get, "health/live"))
        {
            Record("Alive", live.IsSuccessStatusCode, $"{(int)live.StatusCode}");
        }

        string? revision = null;
        if (readyBody is not null)
        {
            using var json = JsonDocument.Parse(readyBody);
            revision = json.RootElement.GetProperty("revision").GetString();
            var checksText = string.Join(", ", json.RootElement.GetProperty("checks").EnumerateObject().Select(p => $"{p.Name} {p.Value.GetString()}"));
            Record("Readiness checks", json.RootElement.GetProperty("status").GetString() == "Healthy", checksText);
        }

        // 2. The build the pipeline deployed, not an older revision still answering at the same address.
        if (expectedRevision is not null)
        {
            Record("Revision", revision == expectedRevision, $"{revision ?? "none"} (expected {expectedRevision})");
        }

        // 3. Sign-in is enforced everywhere, and a forged token is not accepted.
        foreach (var (method, path) in new[] { (HttpMethod.Post, "ask"), (HttpMethod.Post, "chat"), (HttpMethod.Post, "assist"), (HttpMethod.Get, "audit/verify"), (HttpMethod.Post, "mcp") })
        {
            using var anonymous = await Send(http, method, path);
            Record($"{method} /{path} with no token", anonymous.StatusCode == HttpStatusCode.Unauthorized, $"{(int)anonymous.StatusCode}");
        }

        using (var forged = await Send(http, HttpMethod.Post, "ask", ForgedToken))
        {
            Record("POST /ask with a forged token", forged.StatusCode == HttpStatusCode.Unauthorized, $"{(int)forged.StatusCode}");
        }

        // 4. The demo chat page and its personas exist only in Development.
        using (var ui = await Send(http, HttpMethod.Get, "ui"))
        {
            Record("GET /ui is not served", ui.StatusCode == HttpStatusCode.NotFound, $"{(int)ui.StatusCode}");
        }

        // 5. How fast the readiness probe answers, which Container Apps calls every few seconds per replica.
        var timings = new List<double>();
        for (var i = 0; i < 20 && readyBody is not null; i++)
        {
            var sw = Stopwatch.StartNew();
            using var probe = await Send(http, HttpMethod.Get, "health/ready");
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"## Smoke test: {target}")
            .AppendLine()
            .AppendLine("| Check | Result | Detail |")
            .AppendLine("|---|---|---|");
        foreach (var (check, ok, detail) in checks)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"| {check} | {(ok ? "pass" : "FAIL")} | {detail} |");
        }

        report.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Seconds to ready: {secondsToReady:0}")
            .AppendLine(timings.Count == 0 ? "Readiness probe latency: not measured" : string.Create(CultureInfo.InvariantCulture,
                $"Readiness probe latency over {timings.Count} calls: p50 {timings[timings.Count / 2]:0.0} ms, max {timings[^1]:0.0} ms"));
        Directory.CreateDirectory(reportDirectory);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "eval-smoke.md"), report.ToString());
        Console.WriteLine(report);
        return checks.All(c => c.Ok) ? 0 : 1;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, string? token = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("""{"question":"Can I work remotely?","message":"hello"}""", Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request);
    }
}
