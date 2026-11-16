using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PriorAuthBot.Core.Documents;

/// <summary>
/// Reads .docx files (chapter 4). Heading styles become Markdown headings and tables become pipe
/// tables, so the structural chunker from chapter 7 splits Word documents by section, the same way
/// it splits Markdown.
/// </summary>
public static class WordDocumentLoader
{
    public static string ReadText(string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException($"{path} has no body.");
        var text = new StringBuilder();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    var line = paragraph.InnerText.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var level = HeadingLevel(paragraph);
                    text.Append(level > 0 ? new string('#', level) + " " : string.Empty).Append(line).Append("\n\n");
                    break;

                case Table table:
                    var rows = table.Elements<TableRow>().Select(r => "| " + string.Join(" | ", r.Elements<TableCell>().Select(c => c.InnerText.Trim())) + " |").ToList();
                    if (rows.Count > 0)
                    {
                        var columns = table.Elements<TableRow>().First().Elements<TableCell>().Count();
                        rows.Insert(1, "|" + string.Concat(Enumerable.Repeat("---|", columns)));
                        text.Append(string.Join("\n", rows)).Append("\n\n");
                    }

                    break;
            }
        }

        return text.ToString().Trim();
    }

    public static string? ReadTitle(string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        return document.PackageProperties.Title;
    }

    private static int HeadingLevel(Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        return style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style.AsSpan("Heading".Length), out var level) && level is >= 1 and <= 6
            ? level
            : 0;
    }
}
