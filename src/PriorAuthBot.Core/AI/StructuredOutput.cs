using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PriorAuthBot.Core.AI;

/// <summary>
/// Structured output you can trust (chapter 3): ask for <typeparamref name="T"/> with a JSON
/// schema, parse it strictly, validate it with DataAnnotations, and when it fails, show the model
/// what was wrong and ask again.
/// </summary>
public static class StructuredOutput
{
    /// <summary>
    /// The serializer settings for model output. On top of Microsoft.Extensions.AI's defaults,
    /// a missing constructor parameter or a null for a non-nullable one is an error, instead of
    /// silently becoming <c>default</c>.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static async Task<T> GetValidatedResponseAsync<T>(
        this IChatClient chatClient,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        int maxAttempts = 2,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        List<ChatMessage> conversation = [.. messages];
        IReadOnlyList<string> errors = [];

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await chatClient.GetResponseAsync<T>(conversation, SerializerOptions, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            errors = Check(response, out var result);
            if (errors.Count == 0)
            {
                return result!;
            }

            // Keep the bad reply in the conversation so the model can see what it got wrong.
            conversation.AddRange(response.Messages);
            conversation.Add(new ChatMessage(ChatRole.User,
                "Your reply was not valid:\n- " + string.Join("\n- ", errors) + "\nReply again with only JSON that matches the schema."));
        }

        throw new StructuredOutputException(typeof(T), maxAttempts, errors);
    }

    /// <summary>Returns the problems with a response, or an empty list when it is valid.</summary>
    public static IReadOnlyList<string> Check<T>(ChatResponse<T> response, out T? result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(response);
        result = null;
        try
        {
            result = response.Result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // JsonException messages give a path and position, not the content, so they are safe to log.
            return [$"The reply is not valid JSON for the schema ({ex.Message})."];
        }

        if (result is null)
        {
            return ["The reply was empty or null."];
        }

        var validationResults = new List<ValidationResult>();
        return Validator.TryValidateObject(result, new ValidationContext(result), validationResults, validateAllProperties: true)
            ? []
            : validationResults.Select(r => r.ErrorMessage ?? "Validation failed.").ToList();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };
        options.MakeReadOnly();
        return options;
    }
}
