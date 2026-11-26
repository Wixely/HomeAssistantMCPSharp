namespace HomeAssistantMCPSharp.Configuration;

internal sealed class SecretProtectionService
{
    private readonly IReadOnlyDictionary<string, ISecretProtector> _protectors;
    private readonly ISecretProtector? _active;

    public SecretProtectionService(IEnumerable<ISecretProtector> protectors, string activeScheme)
    {
        _protectors = protectors.ToDictionary(p => p.Scheme, StringComparer.OrdinalIgnoreCase);
        if (string.Equals(activeScheme, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_protectors.TryGetValue(activeScheme, out _active))
        {
            throw new InvalidOperationException(
                $"Secret protection scheme '{activeScheme}' is unavailable. Available schemes: " +
                $"{string.Join(", ", _protectors.Keys)}; use 'none' to disable protection.");
        }
    }

    public bool IsEnabled => _active is not null;
    public string ActiveScheme => _active?.Scheme ?? "none";

    public string Protect(string plaintext) => _active?.Protect(plaintext)
        ?? throw new InvalidOperationException("Secret protection is disabled.");

    public string Unprotect(string ciphertext, string? scheme)
    {
        var selectedScheme = string.IsNullOrWhiteSpace(scheme) ? _active?.Scheme : scheme;
        if (selectedScheme is null || !_protectors.TryGetValue(selectedScheme, out var protector))
        {
            throw new InvalidOperationException(
                $"No available protector can decrypt a secret using scheme '{selectedScheme ?? "(unspecified)"}'.");
        }

        return protector.Unprotect(ciphertext);
    }
}
