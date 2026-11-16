namespace PriorAuthBot.Tests.Infrastructure;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SampleDocuments => Path.Combine(RepoRoot, "samples", "documents");

    public static string SampleFiles => Path.Combine(RepoRoot, "samples", "files");

    public static string SampleIntake => Path.Combine(RepoRoot, "samples", "intake");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MasteringAI.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find MasteringAI.slnx above the test output folder.");
    }
}
