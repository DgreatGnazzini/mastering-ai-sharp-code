namespace PriorAuthBot.Core.Documents;

/// <summary>How PriorAuthBot reads documents that are not plain text (chapter 4).</summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    /// <summary>
    /// <c>Vision</c> (the keyed vision chat client), <c>DocumentIntelligence</c> (Azure AI Document
    /// Intelligence), or <c>None</c>, which skips scanned pages and logs them.
    /// </summary>
    public string Ocr { get; set; } = "Vision";

    /// <summary>The Document Intelligence resource, such as <c>https://dd-docs.cognitiveservices.azure.com/</c>.</summary>
    public Uri? DocumentIntelligenceEndpoint { get; set; }
}
