using System.Text.Json.Nodes;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Configuration;

namespace HomeAssistantMCPSharp.Tests;

public sealed class SecretProtectionTests
{
    [Fact]
    public void AesGcmProtector_RoundTripsWithPersistentKey()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var keyPath = Path.Combine(temporaryDirectory.Path, "master.key");
        var first = new AesGcmKeyFileSecretProtector(keyPath);

        var protectedValue = first.Protect("test-secret");
        var second = new AesGcmKeyFileSecretProtector(keyPath);

        Assert.NotEqual("test-secret", protectedValue);
        Assert.Equal("test-secret", second.Unprotect(protectedValue));
        Assert.Equal(AesGcmKeyFileSecretProtector.KeyLengthBytes, File.ReadAllBytes(keyPath).Length);
    }

    [Fact]
    public void DpapiCurrentUserProtector_RoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiCurrentUserSecretProtector();
        var protectedValue = protector.Protect("test-secret");

        Assert.NotEqual("test-secret", protectedValue);
        Assert.Equal("test-secret", protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Bootstrapper_ProtectsLocalSecretsAndRestoresThemAfterRestart()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configurationPath = Path.Combine(temporaryDirectory.Path, "HomeAssistantMCPSharp.Local.json");
        File.WriteAllText(
            configurationPath,
            """
            {
              "HomeAssistant": { "AccessToken": "test-token", "ReadOnly": true },
              "Server": { "Password": "test-mcp-password" },
              "Development": {
                "Sftp": {
                  "Password": "test-sftp-password",
                  "PrivateKeyPassphrase": "test-passphrase",
                  "Host": "homeassistant.test"
                }
              }
            }
            """);

        var firstConfiguration = LoadConfiguration(configurationPath);
        var firstResult = SecretProtectionBootstrapper.Apply(
            firstConfiguration,
            temporaryDirectory.Path,
            configurationPath);

        Assert.Equal("aesgcm-keyfile", firstResult.ActiveScheme);
        Assert.Equal(4, firstResult.ProtectedSecretCount);
        Assert.Equal("test-token", firstConfiguration["HomeAssistant:AccessToken"]);
        Assert.Equal("test-mcp-password", firstConfiguration["Server:Password"]);
        Assert.Equal("test-sftp-password", firstConfiguration["Development:Sftp:Password"]);
        Assert.Equal("test-passphrase", firstConfiguration["Development:Sftp:PrivateKeyPassphrase"]);

        var rewritten = JsonNode.Parse(File.ReadAllText(configurationPath))!.AsObject();
        Assert.Null(rewritten["HomeAssistant"]!["AccessToken"]);
        Assert.NotNull(rewritten["HomeAssistant"]!["AccessTokenProtected"]);
        Assert.Null(rewritten["Server"]!["Password"]);
        Assert.NotNull(rewritten["Server"]!["PasswordProtected"]);
        Assert.Null(rewritten["Development"]!["Sftp"]!["Password"]);
        Assert.NotNull(rewritten["Development"]!["Sftp"]!["PasswordProtected"]);
        Assert.Null(rewritten["Development"]!["Sftp"]!["PrivateKeyPassphrase"]);
        Assert.Equal("homeassistant.test", rewritten["Development"]!["Sftp"]!["Host"]!.GetValue<string>());

        var secondConfiguration = LoadConfiguration(configurationPath);
        var secondResult = SecretProtectionBootstrapper.Apply(
            secondConfiguration,
            temporaryDirectory.Path,
            configurationPath);

        Assert.Equal(0, secondResult.ProtectedSecretCount);
        Assert.Equal("test-token", secondConfiguration["HomeAssistant:AccessToken"]);
        Assert.Equal("test-mcp-password", secondConfiguration["Server:Password"]);
        Assert.Equal("test-sftp-password", secondConfiguration["Development:Sftp:Password"]);
        Assert.Equal("test-passphrase", secondConfiguration["Development:Sftp:PrivateKeyPassphrase"]);

        var blankOverrideConfiguration = LoadConfiguration(configurationPath);
        blankOverrideConfiguration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:Password"] = string.Empty,
        });
        SecretProtectionBootstrapper.Apply(
            blankOverrideConfiguration,
            temporaryDirectory.Path,
            configurationPath);

        Assert.Equal(string.Empty, blankOverrideConfiguration["Server:Password"]);
    }

    [Fact]
    public void Bootstrapper_DoesNotCopyEnvironmentStyleOverrideIntoLocalFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var configurationPath = Path.Combine(temporaryDirectory.Path, "HomeAssistantMCPSharp.Local.json");
        File.WriteAllText(configurationPath, "{ \"Server\": { \"Host\": \"localhost\" } }");
        var configuration = LoadConfiguration(configurationPath);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:Password"] = "runtime-only-password",
        });

        var result = SecretProtectionBootstrapper.Apply(configuration, temporaryDirectory.Path, configurationPath);

        Assert.Equal(0, result.ProtectedSecretCount);
        Assert.Equal("runtime-only-password", configuration["Server:Password"]);
        Assert.DoesNotContain("Password", File.ReadAllText(configurationPath), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "master.key")));
    }

    private static ConfigurationManager LoadConfiguration(string path)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SecretProtection:CredentialProtection"] = "aesgcm-keyfile",
            ["SecretProtection:AutoProtectCredentials"] = "true",
            ["SecretProtection:KeyFilePath"] = "master.key",
        });
        return configuration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "HomeAssistantMCPSharp.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
