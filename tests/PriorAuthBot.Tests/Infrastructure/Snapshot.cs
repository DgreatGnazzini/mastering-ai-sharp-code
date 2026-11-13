using Xunit;

namespace PriorAuthBot.Tests.Infrastructure;

/// <summary>
/// A minimal snapshot assertion (chapter 3). The approved output lives in Snapshots/{name}.verified.txt
/// and is committed, so a change to a prompt shows up in the pull request as a diff of exactly what
/// the model will see. On a mismatch the new output is written to {name}.received.txt; review it,
/// and if it is right, rerun with PRIORAUTHBOT_UPDATE_SNAPSHOTS=1 to approve it.
/// </summary>
internal static class Snapshot
{
    private static string Directory => Path.Combine(TestPaths.RepoRoot, "tests", "PriorAuthBot.Tests", "Snapshots");

    public static void Match(string name, string actual)
    {
        var verified = Path.Combine(Directory, $"{name}.verified.txt");
        var received = Path.Combine(Directory, $"{name}.received.txt");
        actual = Normalize(actual);

        if (Environment.GetEnvironmentVariable("PRIORAUTHBOT_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(verified, actual);
            File.Delete(received);
            return;
        }

        var expected = File.Exists(verified) ? Normalize(File.ReadAllText(verified)) : null;
        if (expected == actual)
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, actual);
        Assert.Fail(expected is null
            ? $"No approved snapshot for '{name}'. Review {received}, then rerun with PRIORAUTHBOT_UPDATE_SNAPSHOTS=1."
            : $"Snapshot '{name}' changed. Compare {received} with {verified}; if the change is intended, rerun with PRIORAUTHBOT_UPDATE_SNAPSHOTS=1.");
    }

    // Git can check text files out with CRLF on Windows; the comparison should not care.
    private static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd() + "\n";
}
