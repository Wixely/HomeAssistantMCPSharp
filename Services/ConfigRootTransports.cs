using System.Security.Cryptography;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HomeAssistantMCPSharp.Services;

public sealed record ConfigFileEntry(string RelativePath, long Length, DateTimeOffset? ModifiedUtc);

public interface IConfigRootTransport
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<IReadOnlyList<ConfigFileEntry>> ListFilesAsync(string relativeRoot, CancellationToken ct = default);
    Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default);
    Task WriteFileAtomicAsync(string relativePath, byte[] content, CancellationToken ct = default);
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);
}

public sealed class ConfigRootTransportFactory
{
    private readonly DevelopmentOptions _options;

    public ConfigRootTransportFactory(IOptions<DevelopmentOptions> options)
    {
        _options = options.Value;
    }

    public IConfigRootTransport Create() => _options.Transport.Trim().ToLowerInvariant() switch
    {
        "local" => new LocalConfigRootTransport(_options.LocalConfigRoot),
        "sftp" => new SftpConfigRootTransport(_options.Sftp, _options.MaxFileBytes),
        _ => new DisabledConfigRootTransport(),
    };
}

internal sealed class DisabledConfigRootTransport : IConfigRootTransport
{
    public string Name => "None";
    public bool IsConfigured => false;
    private static InvalidOperationException Disabled() =>
        new("Development deployment transport is not configured. Set Development:Transport to Local or Sftp.");
    public Task<IReadOnlyList<ConfigFileEntry>> ListFilesAsync(string relativeRoot, CancellationToken ct = default) => throw Disabled();
    public Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default) => throw Disabled();
    public Task WriteFileAtomicAsync(string relativePath, byte[] content, CancellationToken ct = default) => throw Disabled();
    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default) => throw Disabled();
}

public sealed class LocalConfigRootTransport : IConfigRootTransport
{
    private readonly string _root;

    public LocalConfigRootTransport(string root)
    {
        _root = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.GetFullPath(root);
        if (_root.Length > 0 && Directory.Exists(_root) &&
            (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Development:LocalConfigRoot may not be a symbolic link or reparse point.");
        }
    }

    public string Name => "Local";
    public bool IsConfigured => _root.Length > 0 && Directory.Exists(_root);

    public Task<IReadOnlyList<ConfigFileEntry>> ListFilesAsync(string relativeRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = Resolve(relativeRoot, allowMissing: true);
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<ConfigFileEntry>>([]);
        }

        EnsureNoReparsePoints(root);
        var files = new List<ConfigFileEntry>();
        ListRecursive(root, files, ct);
        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal));
        return Task.FromResult<IReadOnlyList<ConfigFileEntry>>(files);
    }

    public Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath, allowMissing: false);
        EnsureNoReparsePoints(path);
        return File.ReadAllBytesAsync(path, ct);
    }

    public async Task WriteFileAtomicAsync(string relativePath, byte[] content, CancellationToken ct = default)
    {
        var path = Resolve(relativePath, allowMissing: true);
        EnsureNoReparsePoints(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".hamcp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Resolve(relativePath, allowMissing: true);
        if (File.Exists(path))
        {
            EnsureNoReparsePoints(path);
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string Resolve(string relativePath, bool allowMissing)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Development:LocalConfigRoot must name an existing directory.");
        }

        var normalized = NormalizeTransportPath(relativePath);
        var path = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested path escapes the configured Home Assistant root.");
        }

        if (!allowMissing && !File.Exists(path)) throw new FileNotFoundException("Deployment file was not found.", relativePath);
        return path;
    }

    private void EnsureNoReparsePoints(string path)
    {
        var current = path;
        while (!string.Equals(current, _root, StringComparison.OrdinalIgnoreCase) && current.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Deployment paths may not traverse symbolic links or reparse points.");
            }
            current = Path.GetDirectoryName(current) ?? _root;
        }
    }

    private void ListRecursive(string directory, List<ConfigFileEntry> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (output.Count > 1000) throw new InvalidOperationException("Development target contains more than 1000 files.");
        EnsureNoReparsePoints(directory);
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            EnsureNoReparsePoints(childDirectory);
            ListRecursive(childDirectory, output, ct);
        }
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (output.Count >= 1000) throw new InvalidOperationException("Development target contains more than 1000 files.");
            EnsureNoReparsePoints(path);
            var info = new FileInfo(path);
            output.Add(new ConfigFileEntry(
                Path.GetRelativePath(_root, path).Replace('\\', '/'),
                info.Length,
                info.LastWriteTimeUtc));
        }
    }

    internal static string NormalizeTransportPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Contains(':') ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("Invalid relative configuration path.");
        }
        return normalized;
    }
}

public sealed class SftpConfigRootTransport : IConfigRootTransport
{
    private readonly SftpDevelopmentOptions _options;
    private readonly long _maxReadBytes;

    public SftpConfigRootTransport(SftpDevelopmentOptions options, long maxReadBytes = 8 * 1024 * 1024)
    {
        _options = options;
        _maxReadBytes = Math.Clamp(maxReadBytes, 4096, 128 * 1024 * 1024);
    }

    public string Name => "Sftp";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Host) &&
        !string.IsNullOrWhiteSpace(_options.Username) &&
        !string.IsNullOrWhiteSpace(_options.HostKeySha256) &&
        _options.ConfigRoot.StartsWith('/');

    public Task<IReadOnlyList<ConfigFileEntry>> ListFilesAsync(string relativeRoot, CancellationToken ct = default) =>
        RunAsync(client =>
        {
            var root = Resolve(relativeRoot);
            if (!client.Exists(root)) return (IReadOnlyList<ConfigFileEntry>)[];
            RejectSymbolicPath(client, root);
            var output = new List<ConfigFileEntry>();
            ListRecursive(client, root, output);
            return output.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        }, ct);

    public Task<byte[]> ReadFileAsync(string relativePath, CancellationToken ct = default) =>
        RunAsync(client =>
        {
            var path = Resolve(relativePath);
            RejectSymbolicPath(client, path);
            if (client.GetAttributes(path).Size > _maxReadBytes)
                throw new InvalidOperationException($"Remote deployment file exceeds the {_maxReadBytes}-byte read limit.");
            using var stream = new MemoryStream();
            client.DownloadFile(path, stream);
            if (stream.Length > _maxReadBytes)
                throw new InvalidOperationException($"Remote deployment file exceeds the {_maxReadBytes}-byte read limit.");
            return stream.ToArray();
        }, ct);

    public Task WriteFileAtomicAsync(string relativePath, byte[] content, CancellationToken ct = default) =>
        RunAsync<object?>(client =>
        {
            var path = Resolve(relativePath);
            EnsureParentDirectories(client, GetDirectory(path));
            RejectSymbolicPath(client, GetDirectory(path));
            var temporary = path + ".hamcp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var previous = path + ".hamcp-old-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var movedPrevious = false;
            try
            {
                using var stream = new MemoryStream(content, writable: false);
                client.UploadFile(stream, temporary, canOverride: false);
                if (client.Exists(path))
                {
                    client.RenameFile(path, previous);
                    movedPrevious = true;
                }
                client.RenameFile(temporary, path);
                if (movedPrevious) client.DeleteFile(previous);
            }
            catch
            {
                if (!client.Exists(path) && movedPrevious && client.Exists(previous)) client.RenameFile(previous, path);
                throw;
            }
            finally
            {
                if (client.Exists(temporary)) client.DeleteFile(temporary);
                if (client.Exists(previous) && client.Exists(path)) client.DeleteFile(previous);
            }
            return null;
        }, ct);

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default) =>
        RunAsync<object?>(client =>
        {
            var path = Resolve(relativePath);
            if (client.Exists(path))
            {
                RejectSymbolicPath(client, path);
                client.DeleteFile(path);
            }
            return null;
        }, ct);

    private async Task<T> RunAsync<T>(Func<SftpClient, T> action, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("SFTP deployment requires host, username, host-key fingerprint, and an absolute config root.");
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var client = CreateClient();
            client.Connect();
            try { return action(client); }
            finally { if (client.IsConnected) client.Disconnect(); }
        }, ct);
    }

    private SftpClient CreateClient()
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(_options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_options.PrivateKeyPath)
                : new PrivateKeyFile(_options.PrivateKeyPath, _options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(_options.Username, key));
        }
        if (!string.IsNullOrEmpty(_options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(_options.Username, _options.Password));
        }
        if (methods.Count == 0) throw new InvalidOperationException("SFTP deployment requires a password or private key.");

        var client = new SftpClient(new ConnectionInfo(_options.Host, _options.Port, _options.Username, methods.ToArray()));
        client.HostKeyReceived += (_, args) =>
        {
            var expected = NormalizeFingerprint(_options.HostKeySha256);
            var actual = Convert.ToHexString(SHA256.HashData(args.HostKey)).ToLowerInvariant();
            args.CanTrust = CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(actual));
        };
        return client;
    }

    private string Resolve(string relativePath) =>
        _options.ConfigRoot.TrimEnd('/') + "/" + LocalConfigRootTransport.NormalizeTransportPath(relativePath);

    private void ListRecursive(SftpClient client, string directory, List<ConfigFileEntry> output)
    {
        if (output.Count > 1000) throw new InvalidOperationException("Development target contains more than 1000 files.");
        foreach (var entry in client.ListDirectory(directory))
        {
            if (entry.Name is "." or "..") continue;
            if (entry.IsSymbolicLink) throw new InvalidOperationException("Deployment roots may not contain symbolic links.");
            if (entry.IsDirectory)
            {
                ListRecursive(client, entry.FullName, output);
            }
            else if (entry.IsRegularFile)
            {
                if (output.Count >= 1000) throw new InvalidOperationException("Development target contains more than 1000 files.");
                var relative = entry.FullName[(_options.ConfigRoot.TrimEnd('/').Length + 1)..];
                output.Add(new ConfigFileEntry(relative, entry.Length, entry.LastWriteTimeUtc));
            }
        }
    }

    private void RejectSymbolicPath(SftpClient client, string path)
    {
        var root = _options.ConfigRoot.TrimEnd('/');
        if (client.Exists(root) && client.GetAttributes(root).IsSymbolicLink)
        {
            throw new InvalidOperationException("Development:Sftp:ConfigRoot may not be a symbolic link.");
        }
        if (string.Equals(path, root, StringComparison.Ordinal)) return;
        if (!path.StartsWith(root + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested path escapes the configured Home Assistant root.");
        }
        var current = root;
        foreach (var segment in path[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (client.Exists(current) && client.GetAttributes(current).IsSymbolicLink)
            {
                throw new InvalidOperationException("Deployment paths may not traverse symbolic links.");
            }
        }
    }

    private static void EnsureParentDirectories(SftpClient client, string directory)
    {
        var current = directory.StartsWith('/') ? "/" : string.Empty;
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current == "/" ? current + segment : current + "/" + segment;
            if (client.Exists(current))
            {
                if (client.GetAttributes(current).IsSymbolicLink)
                    throw new InvalidOperationException("Deployment paths may not traverse symbolic links.");
            }
            else
            {
                client.CreateDirectory(current);
            }
        }
    }

    private static string GetDirectory(string path) => path[..path.LastIndexOf('/')];

    private static string NormalizeFingerprint(string fingerprint)
    {
        var normalized = new string(fingerprint.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (normalized.Length != 64) throw new InvalidOperationException("Development:Sftp:HostKeySha256 must contain a SHA-256 fingerprint.");
        return normalized;
    }
}
