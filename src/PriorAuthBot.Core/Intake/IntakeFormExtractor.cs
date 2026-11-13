using Microsoft.Extensions.AI;
using PriorAuthBot.Core.AI;
using PriorAuthBot.Core.Prompts;

namespace PriorAuthBot.Core.Intake;

/// <summary>
/// Pulls the fields out of a prior authorization form (chapter 4). Two routes:
/// <list type="bullet">
/// <item>An image goes straight to a vision model, which reads and extracts in one call.</item>
/// <item>Text from OCR (Document Intelligence) goes to a text model, which extracts.</item>
/// </list>
/// Both use the same prompt, the same typed result, and the same validation and retry as chapter 3.
/// </summary>
public sealed class IntakeFormExtractor(IChatClient chatClient)
{
    public Task<IntakeForm> ExtractFromImageAsync(BinaryData image, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ChatMessage form = new(ChatRole.User, [new TextContent("Extract the fields from this form."), new DataContent(image.ToMemory(), mediaType)]);
        return ExtractAsync(form, cancellationToken);
    }

    public Task<IntakeForm> ExtractFromTextAsync(string ocrText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ocrText);
        ChatMessage form = new(ChatRole.User, $"Extract the fields from this form.\n<form>\n{ocrText.Replace("</form>", string.Empty, StringComparison.OrdinalIgnoreCase)}\n</form>");
        return ExtractAsync(form, cancellationToken);
    }

    private Task<IntakeForm> ExtractAsync(ChatMessage form, CancellationToken cancellationToken) =>
        chatClient.GetValidatedResponseAsync<IntakeForm>(
            [new ChatMessage(ChatRole.System, PromptLibrary.Get(PromptLibrary.ExtractIntakeForm)), form],
            new ChatOptions { Temperature = 0 },
            maxAttempts: 2,
            cancellationToken);
}
