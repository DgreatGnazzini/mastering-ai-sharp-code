namespace PriorAuthBot.Eval;

/// <summary>Files the evaluation reads, found from the repository root so it runs from any folder.</summary>
internal static class EvalPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string GoldenSet => Path.Combine(RepoRoot, "eval", "datasets", "golden-set.json");

    public static string Baselines => Path.Combine(RepoRoot, "eval", "baselines.json");

    public static string ToolCases => Path.Combine(RepoRoot, "eval", "datasets", "tool-cases.json");
    public static string AgentCases => Path.Combine(RepoRoot, "eval", "datasets", "agent-cases.json");

    public static string RedTeamCases => Path.Combine(RepoRoot, "eval", "datasets", "red-team-cases.json");

    public static string CacheCases => Path.Combine(RepoRoot, "eval", "datasets", "cache-cases.json");

    public static string RedTeamDocuments => Path.Combine(RepoRoot, "eval", "datasets", "red-team");

    public static string ApiSettings => Path.Combine(RepoRoot, "src", "PriorAuthBot.Api", "appsettings.json");

    public static string MockHrEmployees => Path.Combine(RepoRoot, "samples", "mock-apis", "DeductibleDynamics.MockHr", "employees.json");

    public static string SampleDocuments => Path.Combine(RepoRoot, "samples", "documents");

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MasteringAI.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not find MasteringAI.slnx above the eval output folder or the current folder.");
    }
}
