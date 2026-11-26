using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace HomeAssistantMCPSharp.Configuration;

internal sealed record SecretProtectionBootstrapResult(string ActiveScheme, int ProtectedSecretCount);

internal static class SecretProtectionBootstrapper
{
    public static SecretProtectionBootstrapResult Apply(
        ConfigurationManager configuration,
        string contentRoot,
        string localConfigurationPath,
        Action<string>? onKeyCreated = null)
    {
        var options = configuration.GetSection(SecretProtectionOptions.SectionName)
            .Get<SecretProtectionOptions>() ?? new SecretProtectionOptions();
        var activeScheme = ResolveScheme(options.CredentialProtection);
        var keyPath = ResolvePath(contentRoot, options.KeyFilePath);

        var protectors = new List<ISecretProtector>
        {
            new AesGcmKeyFileSecretProtector(keyPath, onKeyCreated),
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            protectors.Add(new DpapiCurrentUserSecretProtector());
            protectors.Add(new DpapiLocalMachineSecretProtector());
        }

        var protection = new SecretProtectionService(protectors, activeScheme);
        var protectedCount = 0;
        if (options.AutoProtectCredentials)
        {
            protectedCount = new LocalConfigurationSecretRewriter(protection)
                .ProtectPlaintextSecrets(localConfigurationPath);
        }

        var runtimeSecrets = new Dictionary<string, string?>();
        foreach (var secret in LocalConfigurationSecretRewriter.Secrets)
        {
            if (TryResolveRuntimeSecret(configuration, protection, secret, out var plaintext))
            {
                runtimeSecrets[secret.PlaintextConfigurationKey] = plaintext;
            }
        }

        if (runtimeSecrets.Count > 0)
        {
            configuration.AddInMemoryCollection(runtimeSecrets);
        }

        return new SecretProtectionBootstrapResult(protection.ActiveScheme, protectedCount);
    }

    private static bool TryResolveRuntimeSecret(
        ConfigurationManager configuration,
        SecretProtectionService protection,
        ConfigurationSecretDefinition secret,
        out string? plaintext)
    {
        foreach (var provider in ((IConfigurationRoot)configuration).Providers.Reverse())
        {
            if (provider.TryGet(secret.PlaintextConfigurationKey, out plaintext))
            {
                return true;
            }

            if (!provider.TryGet(secret.ProtectedConfigurationKey, out var protectedValue))
            {
                continue;
            }

            if (string.IsNullOrEmpty(protectedValue))
            {
                plaintext = string.Empty;
                return true;
            }

            provider.TryGet(secret.SchemeConfigurationKey, out var scheme);
            plaintext = protection.Unprotect(protectedValue, scheme);
            return true;
        }

        plaintext = null;
        return false;
    }

    private static string ResolveScheme(string configuredScheme)
    {
        if (!string.Equals(configuredScheme, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configuredScheme;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dpapi-user" : "aesgcm-keyfile";
    }

    private static string ResolvePath(string contentRoot, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("A secret-protection path cannot be blank.");
        }

        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRoot, configuredPath));
    }
}
