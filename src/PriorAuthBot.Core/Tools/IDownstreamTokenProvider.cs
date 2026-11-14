namespace PriorAuthBot.Core.Tools;

/// <summary>
/// Gets an access token for a downstream API, for one user (chapter 10). There is deliberately no
/// overload without a <see cref="ToolCaller"/>: PriorAuthBot has no token of its own for the HR API,
/// so it cannot read anyone's data unless that person is the one asking.
/// </summary>
public interface IDownstreamTokenProvider
{
    ValueTask<string> GetTokenAsync(ToolCaller caller, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default);
}
