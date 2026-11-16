using System.Text.Json;
using System.Text.RegularExpressions;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests.Deployment;

/// <summary>
/// Chapter 14: the security reviewer's questions about the infrastructure, as tests over the Bicep
/// and the workflows. They read the text, so they run in milliseconds on every OS with no Azure and
/// no Bicep CLI; the CI "Infrastructure" job compiles the templates. A change that turns key auth
/// back on, opens a service to the internet, or unpins an action fails the build.
/// </summary>
public sealed partial class InfrastructureTests
{
    private static string Infra(string path) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "infra", path));

    private static string Workflow(string name) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, ".github", "workflows", name));

    private static IEnumerable<string> AllBicep() =>
        Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot, "infra"), "*.bicep", SearchOption.AllDirectories);

    // Question 1: are any long-lived secrets left?

    [Theory]
    [InlineData("modules/openai.bicep", "disableLocalAuth: true")]
    [InlineData("modules/search.bicep", "disableLocalAuth: true")]
    [InlineData("modules/monitoring.bicep", "disableLocalAuth: true")]
    [InlineData("modules/monitoring.bicep", "DisableLocalAuth: true")]
    [InlineData("modules/storage.bicep", "allowSharedKeyAccess: false")]
    [InlineData("modules/postgres.bicep", "passwordAuth: 'Disabled'")]
    [InlineData("modules/registry.bicep", "adminUserEnabled: false")]
    [InlineData("modules/keyvault.bicep", "enableRbacAuthorization: true")]
    [InlineData("modules/keyvault.bicep", "enablePurgeProtection: true")]
    public void EveryServiceAcceptsOnlyEntraTokens(string file, string setting) =>
        Assert.Contains(setting, Infra(file), StringComparison.Ordinal);

    [Fact]
    public void NoTemplateReadsAKeyOrTakesASecret()
    {
        foreach (var file in AllBicep())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(KeyFunctions(), text);
            Assert.DoesNotContain("@secure()", text, StringComparison.Ordinal);
            Assert.DoesNotContain("anonymousPullEnabled: true", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secretRef", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheApiSignsInToPostgresWithItsIdentityNotAPassword()
    {
        var apps = Infra("modules/containerapps.bicep");
        Assert.Contains("Postgres__Authentication", apps, StringComparison.Ordinal);
        Assert.DoesNotMatch(PasswordInConnectionString(), apps);
    }

    [Fact]
    public void TheDeployWorkflowUsesOidcAndNoAzureSecret()
    {
        foreach (var name in new[] { "deploy.yml", "rollback.yml" })
        {
            var text = Workflow(name);
            Assert.DoesNotContain("secrets.AZURE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("client-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("creds:", text, StringComparison.Ordinal);
            Assert.Contains("client-id: ${{ vars.AZURE_CLIENT_ID }}", text, StringComparison.Ordinal);
        }
    }

    // Question 2: does traffic stay on private networks?

    [Theory]
    [InlineData("modules/openai.bicep")]
    [InlineData("modules/postgres.bicep")]
    [InlineData("modules/storage.bicep")]
    [InlineData("modules/keyvault.bicep")]
    [InlineData("modules/search.bicep")]
    public void EveryDataServiceIsPrivate(string file)
    {
        var text = Infra(file);
        Assert.Matches(PublicAccessDisabled(), text);
        Assert.Contains("private-endpoint.bicep", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApiHasInternalIngressOnly()
    {
        var apps = Infra("modules/containerapps.bicep");
        Assert.Contains("internal: true", apps, StringComparison.Ordinal);
        Assert.Contains("external: false", apps, StringComparison.Ordinal);
        Assert.Contains("allowInsecure: false", apps, StringComparison.Ordinal);
        Assert.DoesNotContain("external: true", apps, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheRegistryIsReachableFromTheInternet()
    {
        var open = AllBicep()
            .Where(f => File.ReadAllText(f).Contains("publicNetworkAccess: 'Enabled'", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Equal(["registry.bicep"], open);
    }

    // Question 3: which gates must pass, and who can override?

    [Fact]
    public void EveryActionInTheReleaseWorkflowsIsPinnedToACommit()
    {
        foreach (var name in new[] { "deploy.yml", "rollback.yml" })
        {
            var uses = UsesLine().Matches(Workflow(name)).Select(m => m.Groups[1].Value).ToArray();
            Assert.NotEmpty(uses);
            Assert.All(uses, u => Assert.Matches(PinnedToSha(), u));
        }
    }

    [Fact]
    public void TheReleaseWorkflowsStartWithNoPermissions()
    {
        Assert.Contains("\npermissions: {}\n", Workflow("deploy.yml").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("\npermissions: {}\n", Workflow("rollback.yml").ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyJobsInTheProductionEnvironmentCanSignInToAzure()
    {
        foreach (var name in new[] { "deploy.yml", "rollback.yml" })
        {
            foreach (var job in Jobs(Workflow(name)))
            {
                if (job.Contains("azure/login@", StringComparison.Ordinal))
                {
                    Assert.Contains("environment: production\n", job, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void TheDeployGatesNameJobsThatExistInCi()
    {
        var ci = Workflow("ci.yml");
        var deploy = Workflow("deploy.yml").ReplaceLineEndings("\n");
        var start = deploy.IndexOf("required=(", StringComparison.Ordinal);
        var end = deploy.IndexOf("\n          )", start, StringComparison.Ordinal);
        var required = RequiredCheck().Matches(deploy[start..end])
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(6, required.Length);
        foreach (var check in required)
        {
            var jobName = check.Replace("(ubuntu-latest)", "(${{ matrix.os }})", StringComparison.Ordinal);
            Assert.Contains($"name: {jobName}\n", ci.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnOverrideNeedsItsOwnEnvironmentAndAReason()
    {
        var deploy = Workflow("deploy.yml");
        Assert.Contains("environment: production-override", deploy, StringComparison.Ordinal);
        Assert.Contains("override_reason", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGitHubIdentityTrustsOnlyTheProductionEnvironment()
    {
        var bootstrap = Infra("bootstrap/github-oidc.sh");
        Assert.Contains("repo:${repo}:environment:production", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain(":ref:", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain(":pull_request", bootstrap, StringComparison.Ordinal);
    }

    // Question 4: how do you roll back a model version?

    [Fact]
    public void ModelVersionsArePinned()
    {
        var openai = Infra("modules/openai.bicep");
        Assert.Contains("versionUpgradeOption: 'NoAutoUpgrade'", openai, StringComparison.Ordinal);
        Assert.DoesNotContain("OnceNewDefaultVersionAvailable", openai, StringComparison.Ordinal);
        Assert.DoesNotContain("OnceCurrentVersionExpired", openai, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseModelIsADeploymentTheTemplateCreates()
    {
        using var release = JsonDocument.Parse(Infra("release.json"));
        var deployment = release.RootElement.GetProperty("chatDeployment").GetString()!;
        var main = Infra("main.bicep");

        Assert.Contains($"name: '{deployment}'", main, StringComparison.Ordinal);
        Assert.Matches(VersionInName(), deployment);
        Assert.InRange(release.RootElement.GetProperty("canaryPercent").GetInt32(), 1, 50);
    }

    [Fact]
    public void ThePipelineRollsBackOnFailureAndKeepsTheStableRevisionRunning()
    {
        var deploy = Workflow("deploy.yml");
        Assert.Contains("if: failure() && steps.canary.outputs.revision != ''", deploy, StringComparison.Ordinal);
        Assert.Contains("--revision-weight \"$STABLE=100\"", deploy, StringComparison.Ordinal);
        Assert.Contains("activeRevisionsMode: 'Multiple'", Infra("modules/containerapps.bicep"), StringComparison.Ordinal);
    }

    // The audit archive is write-once.
    [Fact]
    public void TheAuditArchiveHasAnImmutabilityPolicy()
    {
        var storage = Infra("modules/storage.bicep");
        Assert.Contains("immutabilityPolicies", storage, StringComparison.Ordinal);
        Assert.Contains("immutabilityPeriodSinceCreationInDays: retentionDays", storage, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Jobs(string workflow)
    {
        var text = workflow.ReplaceLineEndings("\n");
        var jobs = text[(text.IndexOf("\njobs:\n", StringComparison.Ordinal) + 7)..];
        return JobStart().Split(jobs).Where(j => j.Length > 0);
    }

    [GeneratedRegex(@"\blist(Keys|AccountSas|ServiceSas|Credentials|AdminKeys|QueryKeys)\s*\(")]
    private static partial Regex KeyFunctions();

    [GeneratedRegex(@"Password\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInConnectionString();

    [GeneratedRegex(@"publicNetworkAccess: '[Dd]isabled'")]
    private static partial Regex PublicAccessDisabled();

    [GeneratedRegex(@"^\s*-?\s*uses:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedToSha();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex RequiredCheck();

    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}$")]
    private static partial Regex VersionInName();

    [GeneratedRegex(@"(?m)^(?=  [a-z][\w-]*:\n)")]
    private static partial Regex JobStart();
}
