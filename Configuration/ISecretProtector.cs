namespace HomeAssistantMCPSharp.Configuration;

/// <summary>Protects short configuration secrets and identifies the scheme used.</summary>
internal interface ISecretProtector
{
    string Scheme { get; }

    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
