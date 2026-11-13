namespace PriorAuthBot.Core.AI;

/// <summary>
/// The model did not return output that fits the schema and passes validation, even after
/// retries. The message lists what was wrong, never the model's raw reply, which may echo
/// whatever the user typed (including PHI).
/// </summary>
public sealed class StructuredOutputException : Exception
{
    public StructuredOutputException()
    {
    }

    public StructuredOutputException(string message)
        : base(message)
    {
    }

    public StructuredOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StructuredOutputException(Type resultType, int attempts, IReadOnlyList<string> errors)
        : base($"The model did not return a valid {resultType.Name} after {attempts} attempt(s): {string.Join(" ", errors)}")
    {
        ResultType = resultType;
        Attempts = attempts;
        Errors = errors;
    }

    public Type? ResultType { get; }

    public int Attempts { get; }

    public IReadOnlyList<string> Errors { get; } = [];
}
