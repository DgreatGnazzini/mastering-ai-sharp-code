using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using PriorAuthBot.Core.Prompts;

namespace PriorAuthBot.Core.Classification;

/// <summary>
/// Classifies a question into a <see cref="QuestionIntent"/> with structured output, validates
/// the result, and retries once when the model returns something that does not fit (chapter 3).
/// </summary>
public sealed class QuestionClassifier(IChatClient chatClient)
{
    private const int MaxAttempts = 2;

    public async Task<QuestionIntent> ClassifyAsync(string question, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, PromptLibrary.Get(PromptLibrary.ClassifyQuestion)),
            new(ChatRole.User, question),
        ];
        var options = new ChatOptions { Temperature = 0 };

        for (var attempt = 1; ; attempt++)
        {
            var response = await chatClient.GetResponseAsync<QuestionIntent>(messages, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.TryGetResult(out var intent) && IsValid(intent))
            {
                return intent;
            }

            if (attempt == MaxAttempts)
            {
                throw new InvalidOperationException("The model did not return a valid QuestionIntent.");
            }

            messages.Add(new(ChatRole.User, "That did not match the schema. Return only JSON that matches it."));
        }
    }

    private static bool IsValid(QuestionIntent intent) =>
        Validator.TryValidateObject(intent, new ValidationContext(intent), null, validateAllProperties: true)
        && Enum.IsDefined(intent.Department)
        && Enum.IsDefined(intent.Sensitivity);
}
