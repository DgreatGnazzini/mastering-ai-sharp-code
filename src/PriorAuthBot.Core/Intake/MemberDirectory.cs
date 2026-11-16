using System.Text.Json;

namespace PriorAuthBot.Core.Intake;

public sealed record Member(string MemberId, string Name, DateOnly DateOfBirth);

/// <summary>
/// The members the plan knows about (chapter 4). In production this is a call to the eligibility
/// system; here it is a JSON file, so the example runs anywhere. Extraction is checked against it:
/// a form whose member ID and date of birth do not match a real member is not processed further.
/// </summary>
public sealed class MemberDirectory(IEnumerable<Member> members)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, Member> _members = members.ToDictionary(m => m.MemberId, StringComparer.Ordinal);

    public static MemberDirectory Load(string path) =>
        new(JsonSerializer.Deserialize<List<Member>>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty."));

    public Member? Find(string? memberId) =>
        memberId is not null && _members.TryGetValue(memberId, out var member) ? member : null;

    public IEnumerable<Member> All => _members.Values;
}
