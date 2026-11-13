using PriorAuthBot.Core.Documents;
using PriorAuthBot.Tests.Infrastructure;
using Xunit;

namespace PriorAuthBot.Tests;

public class DocumentLoaderTests
{
    [Fact]
    public void FrontMatterBecomesAccessMetadata()
    {
        var document = MarkdownDocumentLoader.Parse("pto", """
            ---
            title: Paid Time Off Policy
            policy: HR-104
            department: HR
            allowed-groups: [AllStaff, HR]
            sensitivity: Internal
            phi: false
            ---

            # Paid Time Off Policy

            Body text.
            """, "pto.md");

        Assert.Equal("Paid Time Off Policy", document.Title);
        Assert.Equal("HR-104", document.PolicyNumber);
        Assert.Equal(["AllStaff", "HR"], document.Access.AllowedGroups);
        Assert.False(document.Access.ContainsPhi);
        Assert.StartsWith("# Paid Time Off Policy", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAclMeansNobody()
    {
        var document = MarkdownDocumentLoader.Parse("draft", "---\ntitle: Draft\n---\n\nSecret plans.", "draft.md");

        Assert.True(document.Access.IsDenyAll);
    }

    [Fact]
    public async Task SampleDocumentsAllLoad()
    {
        var documents = await MarkdownDocumentLoader.LoadDirectoryAsync(TestPaths.SampleDocuments, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(15, documents.Count);
        Assert.Single(documents, d => d.Access.IsDenyAll);
        Assert.Single(documents, d => d.Access.ContainsPhi);
    }
}
