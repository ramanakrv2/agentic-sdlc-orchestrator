using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Core.Common;

public static class Hashing
{
    public static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Short(string value, int length = 12) => Sha256(value)[..length];

    /// <summary>Order-independent fingerprint of a set of key/hash pairs.</summary>
    public static string Fingerprint(IEnumerable<KeyValuePair<string, string>> parts) =>
        Sha256(string.Join("|", parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")));
}
