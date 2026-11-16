namespace PriorAuthBot.Core.Identity;

/// <summary>
/// Thrown when a token says the user is in too many groups to list (Entra ID group overage).
/// PriorAuthBot fails closed instead of searching with a partial group list.
/// </summary>
public sealed class GroupOverageException : Exception
{
    public GroupOverageException()
        : base("The token does not list the user's groups (group overage). Configure the app registration to emit only groups assigned to the application, or resolve groups through Microsoft Graph.")
    {
    }

    public GroupOverageException(string message)
        : base(message)
    {
    }

    public GroupOverageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
