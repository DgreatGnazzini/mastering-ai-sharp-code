using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Formats.Html;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace PriorAuthBot.Eval;

/// <summary>
/// PriorAuthBot's evaluation suite (chapter 9):
///   dotnet run --project eval/PriorAuthBot.Eval -- --provider Offline            retrieval and leak checks, no model
///   dotnet run --project eval/PriorAuthBot.Eval -- --provider Offline --answers  plus every answer, from the offline client
///   dotnet run --project eval/PriorAuthBot.Eval -- --provider Ollama --answers   plus LLM judges on the judged sample
///   add --tools to any of them for chapter 10's tool cases (tool choice, arguments, approvals)
///   add --agents for chapter 11's agent cases (the exception workflow and policy agent conversations)
///   add --redteam for chapter 12's attacks, run with the guardrails and again without them
///   add --ops for chapter 13: audit completeness, telemetry content, tokens and cost, and the answer cache
/// Options: --out DIR (reports), --cache DIR (answers, embeddings, judge replies), --baselines FILE,
/// --execution NAME, --no-gate. Exit code 1 means the regression gate failed.
/// </summary>
internal static class EvalProgram
{
    public static async Task<int> Main(string[] args)
    {
        var settings = EvalSettings.Parse(args);
        var set = GoldenSet.Load();
        var leaks = await LeakageChecker.ForSamplesAsync();
        Directory.CreateDirectory(settings.ReportDirectory);
        var cache = settings.Provider == EvalSettings.Ollama ? new FileDistributedCache(Path.Combine(settings.CacheDirectory, "app")) : null;
        Console.WriteLine($"Golden set: {set.Cases.Count} questions, {set.Cases.Count(c => c.IsPermissionCase)} permission cases. Provider: {settings.Provider}.");

        // 1. Retrieval, chapter 7 against chapter 8, on every question.
        var runs = new List<RetrievalRun>();
        foreach (var configuration in RetrievalConfiguration.Comparison(EvalSettings.RerankerModel))
        {
            var config = settings.Configuration(set);
            foreach (var (key, value) in configuration.Settings)
            {
                config[key] = value;
            }

            await using var host = EvalHost.Create(config, cache);
            await host.IngestSamplesAsync();
            var run = await RetrievalEvaluation.RunAsync(configuration, host, set);
            runs.Add(run);
            Console.WriteLine($"{configuration.Name}: {run.Elapsed.TotalSeconds:0.0} s");
        }

        var retrievalReport = Reports.Retrieval(settings.Provider, set, runs);
        await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-retrieval-{settings.Provider.ToLowerInvariant()}.md"), retrievalReport);
        Console.WriteLine(retrievalReport);

        // 2. Answers, from the shipped configuration (hybrid, chapter 8).
        AnswerSummary? answerSummary = null;
        if (settings.Answers)
        {
            await using var host = EvalHost.Create(settings.Configuration(set), cache);
            await host.IngestSamplesAsync();

            ReportingConfiguration? judged = null;
            OllamaSharp.OllamaApiClient? judgeClient = null;
            var cases = set.Cases.AsEnumerable();
            if (settings.Provider == EvalSettings.Ollama)
            {
                // Every permission case (a decline costs no model call) and the judged sample. The other
                // answerable questions are measured by retrieval: a model call each would double the job's time.
                cases = cases.Where(c => c.IsPermissionCase || c.ExpectDecline || c.AnswerEval);
                judgeClient = new OllamaSharp.OllamaApiClient(
                    new HttpClient { BaseAddress = new Uri(EvalSettings.OllamaEndpoint!), Timeout = TimeSpan.FromMinutes(10) }, EvalSettings.JudgeModel);
                judged = DiskBasedReportingConfiguration.Create(
                    storageRootPath: Path.Combine(settings.CacheDirectory, "aieval"),
                    evaluators: AnswerEvaluation.Evaluators(),
                    chatConfiguration: new ChatConfiguration(judgeClient),
                    enableResponseCaching: true,
                    executionName: settings.ExecutionName,
                    tags: [$"chat:{EvalSettings.ChatModel}", $"judge:{EvalSettings.JudgeModel}"]);
            }

            var answers = await AnswerEvaluation.RunAsync(host, set, cases.ToList(), leaks, judged, Console.WriteLine);
            answerSummary = AnswerSummary.Of(answers);
            var answerReport = Reports.Answers(settings.Provider, judged is null ? null : EvalSettings.JudgeModel, answers);
            await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-answers-{settings.Provider.ToLowerInvariant()}.md"), answerReport);
            Console.WriteLine(answerReport);

            if (judged is not null)
            {
                // The HTML report: every judged answer with its scores, the judges' reasons, and the prompt.
                var store = new DiskBasedResultStore(Path.Combine(settings.CacheDirectory, "aieval"));
                var results = new List<ScenarioRunResult>();
                await foreach (var result in store.ReadResultsAsync(settings.ExecutionName))
                {
                    results.Add(result);
                }

                await new HtmlReportWriter(Path.Combine(settings.ReportDirectory, "eval-answers.html")).WriteReportAsync(results);
                Console.WriteLine($"HTML report: {results.Count} scored answers in eval-answers.html.");
            }

            judgeClient?.Dispose();
        }

        // 2b. Tools (chapter 10): which tool the model picks, with which arguments, and that no write runs unapproved.
        ToolSummary? toolSummary = null;
        if (settings.Tools)
        {
            var owned = new List<IAsyncDisposable>();
            await using (var host = EvalHost.Create(settings.Configuration(set), cache, configure: s => ToolEvaluation.Configure(s, owned)))
            {
                await host.IngestSamplesAsync();
                var toolResults = await ToolEvaluation.RunAsync(host, set, ToolCaseSet.Load(), Console.WriteLine);
                toolSummary = ToolSummary.Of(toolResults, ((Core.Tools.InMemoryHelpDesk)host.Get<Core.Tools.IHelpDesk>()).Count);
                var toolReport = Reports.Tools(settings.Provider, toolResults, toolSummary);
                await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-tools-{settings.Provider.ToLowerInvariant()}.md"), toolReport);
                Console.WriteLine(toolReport);
            }

            foreach (var server in owned)
            {
                await server.DisposeAsync();
            }
        }

        // 2c. Agents (chapter 11): where each exception request stops, the hours and approver in its
        // draft, the tool each conversation turn starts with, and that every run stays in its limits.
        AgentSummary? agentSummary = null;
        if (settings.Agents)
        {
            var owned = new List<IAsyncDisposable>();
            await using (var host = EvalHost.Create(settings.Configuration(set), cache, configure: s =>
            {
                ToolEvaluation.Configure(s, owned);
                AgentEvaluation.Configure(s, slowModel: settings.Provider == EvalSettings.Ollama);
            }))
            {
                await host.IngestSamplesAsync();
                var agentResults = await AgentEvaluation.RunAsync(host, set, AgentCaseSet.Load(), Console.WriteLine);
                agentSummary = AgentSummary.Of(agentResults, host.Get<Core.Agents.Exceptions.IExceptionRegister>().All.Count);
                var agentReport = Reports.Agents(settings.Provider, agentResults, agentSummary);
                await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-agents-{settings.Provider.ToLowerInvariant()}.md"), agentReport);
                Console.WriteLine(agentReport);
            }

            foreach (var server in owned)
            {
                await server.DisposeAsync();
            }
        }

        // 2d. Red team (chapter 12): every attack with the guardrails, then again without them. Only the
        // first run is gated; the second shows what the guardrails stop.
        RedTeamSummary? redTeamSummary = null;
        if (settings.RedTeam)
        {
            var cases = RedTeamCaseSet.Load();
            var runsByMode = new Dictionary<bool, IReadOnlyList<RedTeamResult>>();
            foreach (var guardrails in new[] { true, false })
            {
                var owned = new List<IAsyncDisposable>();
                var config = settings.Configuration(set);
                config["Security:Guardrails"] = guardrails ? "true" : "false";
                await using (var host = EvalHost.Create(config, cache, configure: s => RedTeamEvaluation.Configure(s, owned, slowModel: settings.Provider == EvalSettings.Ollama)))
                {
                    await RedTeamEvaluation.IngestAsync(host);
                    Console.WriteLine($"Red team, guardrails {(guardrails ? "on" : "off")}:");
                    runsByMode[guardrails] = await RedTeamEvaluation.RunAsync(host, set, cases, line => Console.WriteLine("  " + line));
                }

                foreach (var server in owned)
                {
                    await server.DisposeAsync();
                }
            }

            redTeamSummary = RedTeamSummary.Of(runsByMode[true]);
            var redTeamReport = Reports.RedTeam(settings.Provider, runsByMode[true], redTeamSummary, runsByMode[false], RedTeamSummary.Of(runsByMode[false]));
            await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-redteam-{settings.Provider.ToLowerInvariant()}.md"), redTeamReport);
            Console.WriteLine(redTeamReport);
        }

        // 2e. Operations (chapter 13): every answer audited, no question in any span, no cached answer
        // across permission sets, and what each answer costs.
        OpsSummary? opsSummary = null;
        if (settings.Ops)
        {
            Console.WriteLine("Observability, audit, and cost:");
            var (summary, opsAnswers, pairs) = await OpsEvaluation.RunAsync(settings, set, cache, Console.WriteLine);
            opsSummary = summary;
            var opsReport = OpsEvaluation.Report(settings.Provider, summary, opsAnswers, pairs);
            await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-ops-{settings.Provider.ToLowerInvariant()}.md"), opsReport);
            Console.WriteLine(opsReport);
        }

        if (cache is not null)
        {
            Console.WriteLine($"Answer and embedding cache: {cache.Hits} hits, {cache.Misses} misses.");
        }

        // 3. The regression gate, on the shipped configuration.
        if (!settings.Gate)
        {
            return 0;
        }

        var shipped = runs.Single(r => r.Configuration.Name == RetrievalConfiguration.Chapter8);
        var gate = RegressionGate.Check(Baselines.Load(settings.BaselinesPath), settings.Provider, shipped.Summary(), runs.Sum(r => r.Leaks), answerSummary, toolSummary, agentSummary, redTeamSummary, opsSummary);
        var gateReport = Reports.Gate(settings.Provider, gate, RegressionGate.Proposed(shipped.Summary(), answerSummary, toolSummary, agentSummary, redTeamSummary));
        await File.WriteAllTextAsync(Path.Combine(settings.ReportDirectory, $"eval-gate-{settings.Provider.ToLowerInvariant()}.md"), gateReport);
        Console.WriteLine(gateReport);
        return gate.Ok ? 0 : 1;
    }
}
