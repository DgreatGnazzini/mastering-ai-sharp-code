using System.Security.Claims;
using Microsoft.Extensions.Options;
using PriorAuthBot.Core.Identity;
using Xunit;

namespace PriorAuthBot.Tests;

public class CallerIdentityFactoryTests
{
    private const string ClinicalId = "00000000-0000-0000-0000-00000000c11c";
    private const string AllStaffId = "00000000-0000-0000-0000-0000000a11a5";

    private static readonly CallerIdentityFactory Factory = new(Options.Create(new GroupMappingOptions
    {
        Groups = new(StringComparer.OrdinalIgnoreCase) { [ClinicalId] = "Clinical", [AllStaffId] = "AllStaff" },
    }));

    [Fact]
    public void GroupObjectIdsMapToAclNames()
    {
        var caller = Factory.Create(Principal(new Claim("groups", ClinicalId), new Claim("groups", AllStaffId), new Claim("groups", "11111111-2222-3333-4444-555555555555")));

        Assert.Equal("user-1", caller.UserId);
        Assert.Equal(["AllStaff", "Clinical"], caller.Groups);
    }

    [Fact]
    public void GroupOverageFailsClosed()
    {
        Assert.Throws<GroupOverageException>(() => Factory.Create(Principal(new Claim("hasgroups", "true"))));
        Assert.Throws<GroupOverageException>(() => Factory.Create(Principal(new Claim("_claim_names", """{"groups":"src1"}"""))));
    }

    [Fact]
    public void AnonymousCallerIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Factory.Create(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim("oid", "user-1"), new Claim("name", "Test User"), .. claims], "Test"));
}
