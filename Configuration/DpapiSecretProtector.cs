using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HomeAssistantMCPSharp.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class DpapiCurrentUserSecretProtector : ISecretProtector
{
    public string Scheme => "dpapi-user";

    public string Protect(string plaintext) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));

    public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), null, DataProtectionScope.CurrentUser));
}

[SupportedOSPlatform("windows")]
internal sealed class DpapiLocalMachineSecretProtector : ISecretProtector
{
    public string Scheme => "dpapi-machine";

    public string Protect(string plaintext) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.LocalMachine));

    public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), null, DataProtectionScope.LocalMachine));
}
