using System.Security.Cryptography;
using System.Text;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Options;

namespace HomeAssistantMCPSharp.Services;

public sealed class DashboardYamlService
{
    private readonly HomeAssistantOptions _options;

    public const string ResourceUri = "homeassistant://dashboard-yaml";

    public DashboardYamlService(IOptions<HomeAssistantOptions> options)
    {
        _options = options.Value;
    }

    public string ResolvePath()
    {
        EnsureEnabled();

        var configuredPath = _options.DashboardYamlPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                "HomeAssistant:DashboardYamlPath is not configured. Set it to the dashboard YAML file path on the MCP host.");
        }

        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HomeAssistant:DashboardYamlPath must point to a .yaml or .yml file.");
        }

        return path;
    }

    public async Task<DashboardYamlSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Dashboard YAML file was not found. Check HomeAssistant:DashboardYamlPath.", path);
        }

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        var info = new FileInfo(path);
        return new DashboardYamlSnapshot(
            path,
            content,
            ComputeSha256(content),
            info.Length,
            info.LastWriteTimeUtc);
    }

    public async Task<DashboardYamlWriteResult> WriteAsync(
        string content,
        string? expectedSha256,
        bool createBackup,
        CancellationToken ct = default)
    {
        EnsureWriteAllowed("ha_update_dashboard_yaml");

        var path = ResolvePath();
        var oldHash = await VerifyExpectedHashAsync(path, expectedSha256, ct);
        var backupPath = createBackup && File.Exists(path) ? CreateBackup(path) : null;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);

        var info = new FileInfo(path);
        return new DashboardYamlWriteResult(
            path,
            backupPath,
            oldHash,
            ComputeSha256(content),
            info.Length,
            info.LastWriteTimeUtc);
    }

    public async Task<DashboardYamlReplaceResult> ReplaceAsync(
        string oldText,
        string newText,
        bool replaceAll,
        string? expectedSha256,
        bool createBackup,
        CancellationToken ct = default)
    {
        EnsureWriteAllowed("ha_replace_dashboard_yaml_text");

        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("oldText is required.", nameof(oldText));
        }

        var snapshot = await ReadAsync(ct);
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(snapshot.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Dashboard YAML changed since it was read. Expected SHA-256 {expectedSha256}, current SHA-256 {snapshot.Sha256}.");
        }

        var count = CountOccurrences(snapshot.Content, oldText);
        if (count == 0)
        {
            throw new InvalidOperationException("oldText was not found in the dashboard YAML.");
        }

        if (!replaceAll && count > 1)
        {
            throw new InvalidOperationException(
                $"oldText occurs {count} times. Set replaceAll=true or provide a more specific oldText.");
        }

        var updated = replaceAll
            ? snapshot.Content.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(snapshot.Content, oldText, newText);

        var write = await WriteAsync(updated, snapshot.Sha256, createBackup, ct);
        return new DashboardYamlReplaceResult(
            write.Path,
            write.BackupPath,
            snapshot.Sha256,
            write.Sha256,
            write.Length,
            write.LastWriteTimeUtc,
            replaceAll ? count : 1);
    }

    private void EnsureEnabled()
    {
        if (!_options.EnableDashboardYaml)
        {
            throw new InvalidOperationException("Dashboard YAML tools are disabled.");
        }
    }

    private void EnsureWriteAllowed(string operation)
    {
        EnsureEnabled();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set HomeAssistant:ReadOnly=false to allow dashboard YAML writes.");
        }
    }

    private static async Task<string?> VerifyExpectedHashAsync(string path, string? expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                throw new InvalidOperationException("Cannot verify expectedSha256 because the dashboard YAML file does not exist.");
            }

            return null;
        }

        var currentContent = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        var currentHash = ComputeSha256(currentContent);
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Dashboard YAML changed since it was read. Expected SHA-256 {expectedSha256}, current SHA-256 {currentHash}.");
        }

        return currentHash;
    }

    private static string CreateBackup(string path)
    {
        var backupPath = $"{path}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        File.Copy(path, backupPath, overwrite: false);
        return backupPath;
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? content
            : string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
    }
}

public sealed record DashboardYamlSnapshot(
    string Path,
    string Content,
    string Sha256,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record DashboardYamlWriteResult(
    string Path,
    string? BackupPath,
    string? PreviousSha256,
    string Sha256,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record DashboardYamlReplaceResult(
    string Path,
    string? BackupPath,
    string PreviousSha256,
    string Sha256,
    long Length,
    DateTime LastWriteTimeUtc,
    int Replacements);
