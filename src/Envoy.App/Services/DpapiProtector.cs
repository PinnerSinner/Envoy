using System.Security.Cryptography;
using System.Text;
using Envoy.Core.Settings;

namespace Envoy.App.Services;

/// <summary>Encrypts API keys with Windows DPAPI so they can only be read by the same Windows user on this PC.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Envoy.Secrets.v1"u8.ToArray();
    private const string Prefix = "dpapi:";

    public string Protect(string plaintext)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedText)
    {
        // Values typed straight into settings.json by hand are accepted as plaintext.
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return protectedText;
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
