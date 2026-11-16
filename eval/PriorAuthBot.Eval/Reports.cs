using System.Globalization;
using System.Text;

namespace PriorAuthBot.Eval;

/// <summary>The Markdown reports CI prints to the job log and uploads with the HTML report.</summary>
internal static class Reports
{
    public static string Retrieval(string provider, GoldenSet set, IReadOnlyList<RetrievalRun> runs)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(runs);
        var top = runs.Count > 0 && runs[0].Results.Count > 0 ? runs[0].Results[0].Top : 6;
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Retrieval, {provider}: {set.Cases.Count} golden questions, top {top} chunks\n\n");
        s.Append(CultureInfo.InvariantCulture, $"| Configuration | Recall@1 | Recall@{top} | MRR | Declines kept | Leaks | Time per question |\n|---|---|---|---|---|---|---|\n");
        foreach (var run in runs)
        {
            var all = run.Summary();
            s.Append(CultureInfo.InvariantCulture,
                $"| {run.Configuration.Name} | {all.RecallAt1:0.00} | {all.RecallAtTop:0.00} | {all.Mrr:0.00} | {all.DeclinesKept}/{all.Declines} | {all.Leaks} | {run.Elapsed.TotalMilliseconds / Math.Max(1, run.Results.Count):0} ms |\n");
        }

        s.Append(CultureInfo.InvariantCulture, $"\nRecall@{top} by kind of question (questions with an expected document):\n\n| Configuration | ");
        var groups = new (string Name, Func<GoldenCase, bool> Filter)[]
        {
            ("Fact", c => c.Kind == GoldenCase.Fact),
            ("Exact term", c => c.IsExactTerm),
            ("Permission", c => c.IsPermissionCase),
            ("Chapters 7-8 questions", c => c.Chapter <= 8),
            ("Chapter 9 questions", c => c.Chapter == 9),
        };
        s.Append(string.Join(" | ", groups.Select(g => $"{g.Name} ({set.Cases.Count(c => g.Filter(c) && c.Expected.Length > 0)})"))).Append(" |\n|---|");
        s.Append(string.Concat(groups.Select(_ => "---|"))).Append('\n');
        foreach (var run in runs)
        {
            s.Append(CultureInfo.InvariantCulture, $"| {run.Configuration.Name} | ");
            s.Append(string.Join(" | ", groups.Select(g => run.Summary(g.Filter).RecallAtTop.ToString("0.00", CultureInfo.InvariantCulture)))).Append(" |\n");
        }

        // Which questions each configuration gets wrong, side by side: the table to read when a number moves.
        var rows = set.Cases
            .Where(c => runs.Any(r => Miss(r.Results.First(x => x.Case.Id == c.Id))))
            .ToList();
        if (rows.Count > 0)
        {
            s.Append("\nQuestions that at least one configuration got wrong (rank of the expected document; – not retrieved; \"answered\" means a question that should be declined returned chunks):\n\n| Question | User | Kind | ");
            s.Append(string.Join(" | ", runs.Select(r => r.Configuration.Name))).Append(" |\n|---|---|---|").Append(string.Concat(runs.Select(_ => "---|"))).Append('\n');
            foreach (var c in rows)
            {
                s.Append(CultureInfo.InvariantCulture, $"| {c.Id} | {c.User} | {c.Kind} | ");
                s.Append(string.Join(" | ", runs.Select(r => Cell(r.Results.First(x => x.Case.Id == c.Id))))).Append(" |\n");
            }
        }

        foreach (var leak in runs.SelectMany(r => r.Results.SelectMany(x => x.Leaks.Select(l => $"{r.Configuration.Name}: {l}"))))
        {
            s.Append("\n- LEAK ").Append(leak);
        }

        return s.ToString();

        static bool Miss(RetrievalCaseResult r) => r.RecallAtTop < 1 || (r.Case.ExpectDecline && r.Returned);

        static string Cell(RetrievalCaseResult r)
        {
            if (r.Case.ExpectDecline)
            {
                return r.Returned ? "answered" : "declined";
            }

            var rank = r.Case.Expected.Length == 0 ? -1 : r.Ranked.ToList().IndexOf(r.Case.Expected[0]);
            return rank < 0 ? "–" : (rank + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    public static string Answers(string provider, string? judge, IReadOnlyList<AnswerCaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var summary = AnswerSummary.Of(results);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Answers, {provider}: {summary.Asked} questions asked, {summary.Scored} scored by the judges{(judge is null ? string.Empty : $" ({judge})")}\n\n");
        s.Append("| Metric | Value |\n|---|---|\n");
        foreach (var (name, value) in summary.Averages)
        {
            s.Append(CultureInfo.InvariantCulture, $"| {name} (average) | {value:0.00} |\n");
        }

        s.Append(CultureInfo.InvariantCulture, $"| Answers containing the expected fact | {summary.ContainsExpected}/{summary.WithExpectedFact} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Answerable questions declined | {summary.WrongDeclines} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Questions that should be declined but were answered | {summary.MissedDeclines} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Leaks in prompts and answers | {summary.Leaks} |\n");

        var scored = results.Where(r => r.Scores.Count > 0).ToList();
        if (scored.Count > 0)
        {
            s.Append("\n| Case | User | ").Append(string.Join(" | ", AnswerEvaluation.MetricNames)).Append(" | Contains expected |\n|---|---|");
            s.Append(string.Concat(AnswerEvaluation.MetricNames.Select(_ => "---|"))).Append("---|\n");
            foreach (var r in scored)
            {
                s.Append(CultureInfo.InvariantCulture, $"| {r.Case.Id} | {r.Case.User} | ");
                s.Append(string.Join(" | ", AnswerEvaluation.MetricNames.Select(m => r.Scores.TryGetValue(m, out var v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "-")));
                s.Append(CultureInfo.InvariantCulture, $" | {r.ContainsExpected?.ToString() ?? "-"} |\n");
            }
        }

        foreach (var r in results.Where(r => r.Declined != r.Case.ExpectDecline && (r.Case.ExpectDecline || r.Case.Expected.Length > 0)))
        {
            s.Append(CultureInfo.InvariantCulture, $"\n- {r.Case.Id}: {(r.Declined ? "declined a question its sources answer" : "answered a question it should decline")}: {r.Answer}");
        }

        foreach (var leak in results.SelectMany(r => r.Leaks))
        {
            s.Append("\n- LEAK ").Append(leak);
        }

        return s.ToString();
    }

    public static string Tools(string provider, IReadOnlyList<ToolCaseResult> results, ToolSummary summary)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(summary);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Tool calls, {provider}: {summary.Cases} tool cases\n\n");
        s.Append("| Metric | Result |\n|---|---|\n");
        s.Append(CultureInfo.InvariantCulture, $"| Right first tool (or none) | {summary.SelectionCorrect} of {summary.Scored} ({summary.Selection:0.00}) |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Right arguments | {summary.ArgumentsCorrect} of {summary.WithArguments} ({summary.Arguments:0.00}) |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Writes held for approval | {summary.WritesHeld} of {summary.ExpectedWrites} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Writes without approval | {summary.UnapprovedWrites} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Leaks in answers | {summary.Leaks} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Errors | {summary.Errors} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Time per case | {(results.Count == 0 ? 0 : results.Average(r => r.Elapsed.TotalSeconds)):0.0} s |\n\n");

        s.Append("| Case | User | Kind | Expected | Calls | Approval | Notes |\n|---|---|---|---|---|---|---|\n");
        foreach (var r in results)
        {
            var calls = r.Reply is null ? "error" : r.Reply.ToolCalls.Count == 0 ? "none" : string.Join(", ", r.Reply.ToolCalls.Select(c => $"{c.Name} {c.Arguments} ({c.Status})"));
            var notes = new List<string>();
            if (r.Case.ScoresSelection && !r.SelectionOk)
            {
                notes.Add("wrong tool");
            }

            if (r.ArgumentsOk == false)
            {
                notes.Add("wrong arguments");
            }

            notes.AddRange(r.Missing.Select(m => $"missing \"{m}\""));
            notes.AddRange(r.Leaks.Select(_ => "LEAK"));
            if (r.Error is not null)
            {
                notes.Add(r.Error);
            }

            s.Append(CultureInfo.InvariantCulture,
                $"| {r.Case.Id} | {r.Case.User} | {r.Case.Kind} | {r.Case.ExpectTool ?? "-"} | {calls.Replace("|", "\\|", StringComparison.Ordinal)} | {(r.Reply?.PendingActions.Count > 0 ? "requested" : "-")} | {string.Join("; ", notes)} |\n");
        }

        foreach (var leak in results.SelectMany(r => r.Leaks))
        {
            s.Append("\n- LEAK ").Append(leak);
        }

        return s.ToString();
    }

    public static string Agents(string provider, IReadOnlyList<AgentCaseResult> results, AgentSummary summary)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(summary);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Agents, {provider}: {results.Count} agent cases\n\n");
        s.Append("| Metric | Result |\n|---|---|\n");
        s.Append(CultureInfo.InvariantCulture, $"| Exception requests that stopped in the right place | {summary.OutcomesCorrect} of {summary.Exceptions} ({summary.Outcome:0.00}) |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Drafts with the right hours | {summary.HoursCorrect} of {summary.WithHours} ({summary.Hours:0.00}) |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Conversation turns with the right first tool | {summary.TurnsCorrect} of {summary.TurnsScored} ({summary.Tools:0.00}) |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Agent writes held for approval | {summary.ApprovalsHeld} of {summary.ExpectedApprovals} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Exceptions decided without the approver | {summary.DecidedWithoutApprover} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Runs over a limit | {summary.OverLimits} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Most model calls in one run | {summary.MaxModelCalls} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Highest cost of one run | ${summary.MaxCost:0.000000} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Leaks in answers | {summary.Leaks} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Errors | {summary.Errors} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Time per case | {(results.Count == 0 ? 0 : results.Average(r => r.Elapsed.TotalSeconds)):0.0} s |\n\n");

        s.Append("| Case | User | Kind | Expected | Result | Model calls | Notes |\n|---|---|---|---|---|---|---|\n");
        foreach (var r in results)
        {
            var expected = r.Case.Kind == AgentCase.Exception
                ? $"{r.Case.ExpectStatus}{(r.Case.ExpectStopCode is null ? string.Empty : $" ({r.Case.ExpectStopCode})")}{(r.Case.ExpectHours is { } h ? $", {h:0.##} h" : string.Empty)}"
                : string.Join(" -> ", r.Case.ExpectTools ?? []);
            var notes = new List<string>();
            if (r.OutcomeOk == false)
            {
                notes.Add("wrong outcome");
            }

            if (r.HoursOk == false)
            {
                notes.Add("wrong hours");
            }

            if (r.TurnsCorrect < r.TurnsScored)
            {
                notes.Add($"{r.TurnsScored - r.TurnsCorrect} wrong tool");
            }

            if (!r.ApprovalOk)
            {
                notes.Add("write not held");
            }

            notes.AddRange(r.Missing.Select(m => $"missing \"{m}\""));
            notes.AddRange(r.Leaks.Select(_ => "LEAK"));
            if (r.Error is not null)
            {
                notes.Add(r.Error);
            }

            s.Append(CultureInfo.InvariantCulture,
                $"| {r.Case.Id} | {r.Case.User} | {r.Case.Kind} | {expected} | {r.Summary.Replace("|", "\\|", StringComparison.Ordinal)} | {r.ModelCalls} | {string.Join("; ", notes)} |\n");
        }

        foreach (var leak in results.SelectMany(r => r.Leaks))
        {
            s.Append("\n- LEAK ").Append(leak);
        }

        return s.ToString();
    }

    /// <summary>Chapter 12: the red-team suite, with the guardrails and without them, side by side.</summary>
    public static string RedTeam(string provider, IReadOnlyList<RedTeamResult> guarded, RedTeamSummary withGuardrails, IReadOnlyList<RedTeamResult>? unguarded, RedTeamSummary? withoutGuardrails)
    {
        ArgumentNullException.ThrowIfNull(guarded);
        ArgumentNullException.ThrowIfNull(withGuardrails);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Red team, {provider}: {guarded.Count} attacks\n\n");
        s.Append("| Metric | With guardrails | Without guardrails |\n|---|---|---|\n");
        string Off(Func<RedTeamSummary, string> f) => withoutGuardrails is null ? "not run" : f(withoutGuardrails);
        s.Append(CultureInfo.InvariantCulture, $"| Blocked before the model | {withGuardrails.Blocked} | {Off(x => x.Blocked.ToString(CultureInfo.InvariantCulture))} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Defended | {withGuardrails.Defended} | {Off(x => x.Defended.ToString(CultureInfo.InvariantCulture))} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Breached (absolute cases) | {withGuardrails.AbsoluteBreaches} | {Off(x => x.AbsoluteBreaches.ToString(CultureInfo.InvariantCulture))} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Breached (measured cases) | {withGuardrails.MeasuredBreaches} | {Off(x => x.MeasuredBreaches.ToString(CultureInfo.InvariantCulture))} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Attack success rate | {withGuardrails.SuccessRate:0.00} | {Off(x => x.SuccessRate.ToString("0.00", CultureInfo.InvariantCulture))} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Errors | {withGuardrails.Errors} | {Off(x => x.Errors.ToString(CultureInfo.InvariantCulture))} |\n");
        s.Append(CultureInfo.InvariantCulture, $"| Time per case | {(guarded.Count == 0 ? 0 : guarded.Average(r => r.Elapsed.TotalSeconds)):0.0} s | {(unguarded is null || unguarded.Count == 0 ? "-" : unguarded.Average(r => r.Elapsed.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " s")} |\n\n");

        s.Append("| OWASP | Cases | Breached with | Breached without |\n|---|---|---|---|\n");
        foreach (var (owasp, (cases, breaches)) in withGuardrails.ByOwasp)
        {
            var off = withoutGuardrails?.ByOwasp.TryGetValue(owasp, out var o) == true ? o.Breaches.ToString(CultureInfo.InvariantCulture) : "-";
            s.Append(CultureInfo.InvariantCulture, $"| {owasp} | {cases} | {breaches} | {off} |\n");
        }

        s.Append("\n| Case | OWASP | Technique | Path | Gate | With guardrails | Without | Findings |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var r in guarded)
        {
            var other = unguarded?.FirstOrDefault(u => u.Case.Id == r.Case.Id);
            var findings = r.Error ?? string.Join("; ", r.Findings);
            if (other is { Outcome: RedTeamOutcome.Breached })
            {
                findings += (findings.Length > 0 ? "; " : string.Empty) + "without: " + string.Join("; ", other.Findings);
            }

            s.Append(CultureInfo.InvariantCulture,
                $"| {r.Case.Id} | {r.Case.Owasp} | {r.Case.Technique} | {r.Case.Path} | {r.Case.Gate} | {r.Outcome} | {other?.Outcome.ToString() ?? "-"} | {findings.Replace("|", "\\|", StringComparison.Ordinal)} |\n");
        }

        // The answers behind every breach, so a reviewer can see what got through without rerunning.
        var breached = guarded.Where(r => r.Outcome == RedTeamOutcome.Breached).Select(r => ("with", r))
            .Concat((unguarded ?? []).Where(r => r.Outcome == RedTeamOutcome.Breached).Select(r => ("without", r)))
            .ToList();
        if (breached.Count > 0)
        {
            s.Append("\nAnswers behind each breach:\n\n");
            foreach (var (mode, r) in breached)
            {
                var answer = r.Answer.ReplaceLineEndings(" ");
                s.Append(CultureInfo.InvariantCulture, $"- {r.Case.Id} ({mode} guardrails): {(answer.Length > 300 ? answer[..300] + "..." : answer)}\n");
            }
        }

        return s.Append('\n').ToString();
    }

    public static string Gate(string provider, GateResult gate, ProviderBaseline proposed)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"### Regression gate, {provider}: {(gate.Ok ? "passed" : "FAILED")}\n\n");
        foreach (var line in gate.Failed)
        {
            s.Append("- FAIL ").Append(line).Append('\n');
        }

        foreach (var line in gate.Passed)
        {
            s.Append("- ok ").Append(line).Append('\n');
        }

        s.Append("\nThis run as a baseline entry (commit it to eval/baselines.json only with a reviewer's approval):\n\n```json\n");
        s.Append(System.Text.Json.JsonSerializer.Serialize(proposed, Baselines.Json)).Append("\n```\n");
        return s.ToString();
    }
}
