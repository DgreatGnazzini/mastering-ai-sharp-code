using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.Documents;

/// <summary>
/// OCR with a vision-capable chat model (chapter 4): the image goes in as <see cref="DataContent"/>
/// and the model writes back what it sees. It handles messy scans well and needs no extra service,
/// but it reports no confidence, and a model can "fix" text it misreads instead of saying it could
/// not read it. Use it for retrieval text, where a wrong word costs a little recall; do not use it
/// alone for fields that drive a decision.
/// </summary>
public sealed class VisionOcrService(IChatClient visionClient) : IOcrService
{
    public const string Instructions = """
        Transcribe all of the text in this image exactly as written, as Markdown.
        Keep headings, numbered lists, and tables. Do not summarize, translate, correct, or add anything.
        If a word is unreadable, write [illegible] in its place.
        Reply with the transcription only.
        """;

    public async Task<IReadOnlyList<DocumentPage>> RecognizeAsync(BinaryData content, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ChatMessage message = new(ChatRole.User, [new TextContent(Instructions), new DataContent(content.ToMemory(), mediaType)]);
        var response = await visionClient.GetResponseAsync([message], new ChatOptions { Temperature = 0 }, cancellationToken).ConfigureAwait(false);
        return [new DocumentPage(1, StripFence(response.Text), Recognized: true)];
    }

    // Small models like to wrap Markdown in a code fence. The fence is not part of the document.
    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine > 0 && lastFence > firstLine ? trimmed[(firstLine + 1)..lastFence].Trim() : trimmed;
    }
}
