using System.Security.Cryptography;
using System.Text;

namespace PriorAuthBot.Core.Caching;

/// <summary>
/// The cache partition an answer belongs to (chapter 13): everything that can change what
/// PriorAuthBot would say. Two callers share cached answers only when their permission sets are
/// exactly the same, because they can retrieve exactly the same documents. A superset is not
/// enough (the cached answer could be missing a document the second caller may see), and a subset
/// is a leak. The model, the prompt version, and the corpus generation are in the key too, so a
/// model swap, a prompt change, or new documents start a fresh partition.
/// </summary>
public static class PermissionPartition
{
    public static string For(IEnumerable<string> groups, string model, string promptVersion, long corpusGeneration)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var permissionSet = string.Join(",", groups.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"v1|{permissionSet}|{model}|{promptVersion}|{corpusGeneration}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }
}
