using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeAssistantMCPSharp.Configuration;

internal sealed record ConfigurationSecretDefinition(
    string PlaintextConfigurationKey,
    string ProtectedConfigurationKey,
    string SchemeConfigurationKey)
{
    public string[] JsonPath => PlaintextConfigurationKey.Split(':');
    public string PlaintextProperty => JsonPath[^1];
    public string ProtectedProperty => ProtectedConfigurationKey.Split(':')[^1];
    public string SchemeProperty => SchemeConfigurationKey.Split(':')[^1];
}

internal sealed class LocalConfigurationSecretRewriter
{
    internal static readonly ConfigurationSecretDefinition[] Secrets =
    [
        new("HomeAssistant:AccessToken", "HomeAssistant:AccessTokenProtected", "HomeAssistant:AccessTokenProtectionScheme"),
        new("Server:Password", "Server:PasswordProtected", "Server:PasswordProtectionScheme"),
        new("Development:Sftp:Password", "Development:Sftp:PasswordProtected", "Development:Sftp:PasswordProtectionScheme"),
        new(
            "Development:Sftp:PrivateKeyPassphrase",
            "Development:Sftp:PrivateKeyPassphraseProtected",
            "Development:Sftp:PrivateKeyPassphraseProtectionScheme"),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly SecretProtectionService _protection;

    public LocalConfigurationSecretRewriter(SecretProtectionService protection)
    {
        _protection = protection;
    }

    public int ProtectPlaintextSecrets(string path)
    {
        if (!_protection.IsEnabled || !File.Exists(path))
        {
            return 0;
        }

        var root = JsonNode.Parse(
            File.ReadAllText(path),
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new JsonException("The local configuration root must be a JSON object.");

        var protectedCount = 0;
        foreach (var secret in Secrets)
        {
            var owner = FindOwner(root, secret.JsonPath);
            if (owner is null || !TryGetString(owner, secret.PlaintextProperty, out var plaintext) ||
                string.IsNullOrWhiteSpace(plaintext))
            {
                continue;
            }

            SetProperty(owner, secret.ProtectedProperty, _protection.Protect(plaintext));
            SetProperty(owner, secret.SchemeProperty, _protection.ActiveScheme);
            RemoveProperty(owner, secret.PlaintextProperty);
            protectedCount++;
        }

        if (protectedCount > 0)
        {
            WriteAtomically(path, root.ToJsonString(SerializerOptions) + Environment.NewLine);
        }

        return protectedCount;
    }

    private static JsonObject? FindOwner(JsonObject root, IReadOnlyList<string> path)
    {
        var current = root;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var child = current.FirstOrDefault(
                p => string.Equals(p.Key, path[index], StringComparison.OrdinalIgnoreCase)).Value;
            if (child is not JsonObject childObject)
            {
                return null;
            }

            current = childObject;
        }

        return current;
    }

    private static bool TryGetString(JsonObject owner, string propertyName, out string value)
    {
        var property = owner.FirstOrDefault(
            p => string.Equals(p.Key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (property.Key is not null && property.Value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var result))
        {
            value = result;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void SetProperty(JsonObject owner, string propertyName, string value)
    {
        RemoveProperty(owner, propertyName);
        owner[propertyName] = value;
    }

    private static void RemoveProperty(JsonObject owner, string propertyName)
    {
        var existingName = owner.Select(p => p.Key).FirstOrDefault(
            key => string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (existingName is not null)
        {
            owner.Remove(existingName);
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(path));
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
