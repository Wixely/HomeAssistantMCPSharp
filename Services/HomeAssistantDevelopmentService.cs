using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HomeAssistantMCPSharp.Services;

public sealed class HomeAssistantDevelopmentService
{
    private readonly DevelopmentOptions _options;
    private readonly HomeAssistantOptions _homeAssistantOptions;
    private readonly IConfigRootTransport _transport;
    private readonly HomeAssistantService _homeAssistant;
    private readonly HomeAssistantWebSocketClient _webSocket;
    private readonly string _dataRoot;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly Regex SensitiveJsonValue = new(
        "(?i)(\\\"(?:password|passwd|token|secret|api[_-]?key|authorization|email|username)\\\"\\s*:\\s*\\\")[^\\\"]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveAssignedValue = new(
        "(?i)\\b(password|passwd|token|secret|api[_-]?key|authorization|email|username)\\b\\s*[=:]\\s*[^\\s,;}\\]]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerValue = new(
        "(?i)bearer\\s+[a-z0-9._~+/-]+=*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public HomeAssistantDevelopmentService(
        IOptions<DevelopmentOptions> options,
        IOptions<HomeAssistantOptions> homeAssistantOptions,
        ConfigRootTransportFactory transportFactory,
        HomeAssistantService homeAssistant,
        HomeAssistantWebSocketClient webSocket,
        IHostEnvironment environment)
    {
        _options = options.Value;
        _homeAssistantOptions = homeAssistantOptions.Value;
        _transport = transportFactory.Create();
        _homeAssistant = homeAssistant;
        _webSocket = webSocket;
        _dataRoot = Path.GetFullPath(Path.IsPathRooted(_options.DataPath)
            ? _options.DataPath
            : Path.Combine(environment.ContentRootPath, _options.DataPath));
        if (_options.Transport.Equals("Local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_options.LocalConfigRoot))
        {
            var configRoot = Path.GetFullPath(_options.LocalConfigRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = configRoot + Path.DirectorySeparatorChar;
            if (string.Equals(_dataRoot, configRoot, StringComparison.OrdinalIgnoreCase) || _dataRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Development:DataPath must be outside the Home Assistant configuration root.");
        }
    }

    public async Task<object> GetEnvironmentAsync(CancellationToken ct)
    {
        EnsureInspection();
        JsonElement? config = null;
        JsonElement? coreState = null;
        string? apiError = null;
        try
        {
            config = await _homeAssistant.GetJsonAsync("api/config", ct);
            coreState = await _homeAssistant.GetJsonAsync("api/core/state", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            apiError = RedactLogLine(ex.Message);
        }

        object? configSummary = null;
        if (config is JsonElement configValue)
        {
            configSummary = new
            {
                version = GetJsonProperty(configValue, "version"),
                state = GetJsonProperty(configValue, "state"),
                components = configValue.TryGetProperty("components", out var components) ? components.Clone() : default(JsonElement?),
            };
        }

        return new
        {
            config = configSummary,
            coreState,
            apiError,
            deployment = new
            {
                transport = _transport.Name,
                configured = _transport.IsConfigured,
                inspectionEnabled = _options.EnableInspection,
                deploymentEnabled = _options.EnableDeployment,
                restartEnabled = _options.EnableRestart,
                readOnly = _homeAssistantOptions.ReadOnly,
                logicalRoots = new[] { "custom_components", "www" },
                _options.MaxChunkBytes,
                _options.MaxFileBytes,
                _options.MaxPackageBytes,
            },
        };
    }

    public async Task<object> ListCustomCardsAsync(CancellationToken ct)
    {
        EnsureInspection();
        var files = await SafeListAsync("www", ct);
        EnsureInventoryBounds(files);
        var resources = await ListResourcesAsync(ct);
        var cards = files
            .GroupBy(file => FirstSegment(file.RelativePath, "www"), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key is not null)
            .Select(group => new
            {
                package = group.Key,
                fileCount = group.Count(),
                bytes = group.Sum(file => file.Length),
                resources = resources.Where(resource => ResourceMatchesPackage(resource.Url, group.Key!)).ToArray(),
            })
            .OrderBy(card => card.package, StringComparer.Ordinal)
            .ToArray();
        return new { cards };
    }

    public async Task<object> GetCustomCardAsync(string packageName, CancellationToken ct)
    {
        EnsureInspection();
        var package = DevelopmentPathPolicy.ValidatePackageName("custom_card", packageName);
        var root = DevelopmentPathPolicy.TargetRoot("custom_card", package);
        var files = await DescribeFilesAsync(root, ct);
        var resources = (await ListResourcesAsync(ct)).Where(resource => ResourceMatchesPackage(resource.Url, package)).ToArray();
        return new { package, files, resources };
    }

    public async Task<object> ListCustomIntegrationsAsync(CancellationToken ct)
    {
        EnsureInspection();
        var files = await SafeListAsync("custom_components", ct);
        EnsureInventoryBounds(files);
        var groups = files.GroupBy(file => FirstSegment(file.RelativePath, "custom_components"), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key is not null);
        var output = new List<object>();
        foreach (var group in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            output.Add(await DescribeIntegrationAsync(group.Key!, group.ToArray(), includeLogs: false, ct));
        }
        return new { integrations = output };
    }

    public async Task<object> GetCustomIntegrationAsync(string domain, bool includeLogs, CancellationToken ct)
    {
        EnsureInspection();
        var package = DevelopmentPathPolicy.ValidatePackageName("custom_integration", domain);
        var root = DevelopmentPathPolicy.TargetRoot("custom_integration", package);
        var files = await SafeListAsync(root, ct);
        return await DescribeIntegrationAsync(package, files, includeLogs, ct);
    }

    public async Task<object> BeginDeploymentAsync(
        string kind,
        string packageName,
        string? entryFile,
        bool removeMissingFiles,
        CancellationToken ct)
    {
        EnsureDeployment();
        kind = DevelopmentPathPolicy.NormalizeKind(kind);
        packageName = DevelopmentPathPolicy.ValidatePackageName(kind, packageName);
        entryFile = string.IsNullOrWhiteSpace(entryFile) ? null : DevelopmentPathPolicy.NormalizeRelativeFile(kind, entryFile);
        var now = DateTimeOffset.UtcNow;
        var deployment = new DevelopmentDeployment
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            PackageName = packageName,
            EntryFile = entryFile,
            RemoveMissingFiles = removeMissingFiles,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(Clamp(_options.SessionLifetimeMinutes, 5, 1440)),
        };
        await SaveAsync(deployment, ct);
        return new
        {
            deployment.Id,
            deployment.Kind,
            deployment.PackageName,
            deployment.EntryFile,
            deployment.RemoveMissingFiles,
            deployment.ExpiresUtc,
            _options.MaxChunkBytes,
            _options.MaxFileBytes,
            _options.MaxPackageBytes,
        };
    }

    public async Task<object> PutFileAsync(
        string deploymentId,
        string relativePath,
        long offset,
        string contentBase64,
        bool isFinal,
        long? finalLength,
        string? finalSha256,
        CancellationToken ct)
    {
        EnsureDeployment();
        await _mutex.WaitAsync(ct);
        try
        {
            var deployment = await LoadAsync(deploymentId, ct);
            EnsureStaging(deployment);
            relativePath = DevelopmentPathPolicy.NormalizeRelativeFile(deployment.Kind, relativePath);
            byte[] chunk;
            try { chunk = Convert.FromBase64String(contentBase64); }
            catch (FormatException) { throw new ArgumentException("contentBase64 is not valid Base64.", nameof(contentBase64)); }
            if (chunk.Length > Clamp(_options.MaxChunkBytes, 4096, 4 * 1024 * 1024))
            {
                throw new InvalidOperationException("Deployment chunk exceeds Development:MaxChunkBytes.");
            }

            var path = StagePath(deployment, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var currentLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (offset != currentLength) throw new InvalidOperationException($"Chunk offset {offset} does not match staged length {currentLength}.");
            if (currentLength + chunk.Length > Clamp(_options.MaxFileBytes, 4096, 128 * 1024 * 1024))
            {
                throw new InvalidOperationException("Deployment file exceeds Development:MaxFileBytes.");
            }
            await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await stream.WriteAsync(chunk, ct);
            }

            var total = deployment.Files.Values.Where(file => file.RelativePath != relativePath).Sum(file => file.Length) + currentLength + chunk.Length;
            if (total > Clamp(_options.MaxPackageBytes, 4096, 512 * 1024 * 1024))
            {
                using (var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    truncate.SetLength(currentLength);
                }
                throw new InvalidOperationException("Deployment package exceeds Development:MaxPackageBytes.");
            }

            var file = deployment.Files.GetValueOrDefault(relativePath) ?? new DevelopmentUploadFile { RelativePath = relativePath };
            file.Length = currentLength + chunk.Length;
            file.Complete = false;
            if (isFinal)
            {
                try
                {
                    if (finalLength is null || finalLength.Value != file.Length || string.IsNullOrWhiteSpace(finalSha256))
                    {
                        throw new InvalidOperationException("Final chunks require the exact finalLength and finalSha256.");
                    }
                    file.Sha256 = await ComputeFileHashAsync(path, ct);
                    if (!string.Equals(file.Sha256, NormalizeSha(finalSha256), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Final deployment file SHA-256 does not match the uploaded content.");
                    }
                    file.Complete = true;
                }
                catch
                {
                    using var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                    truncate.SetLength(currentLength);
                    throw;
                }
            }
            deployment.Files[relativePath] = file;
            deployment.Preview = null;
            deployment.ConfirmationHash = null;
            await SaveUnlockedAsync(deployment, ct);
            return new { deployment.Id, file.RelativePath, file.Length, file.Sha256, file.Complete };
        }
        finally { _mutex.Release(); }
    }

    public async Task<object> PreviewDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        EnsureDeployment();
        await _mutex.WaitAsync(ct);
        try
        {
            var deployment = await LoadAsync(deploymentId, ct);
            EnsureStaging(deployment);
            var preview = await BuildPreviewAsync(deployment, ct);
            var confirmation = IssueConfirmation(deployment, "apply");
            deployment.Preview = preview;
            await SaveUnlockedAsync(deployment, ct);
            return new { deployment.Id, preview, confirmation };
        }
        finally { _mutex.Release(); }
    }

    public async Task<object> ApplyDeploymentAsync(string deploymentId, string confirmationToken, CancellationToken ct)
    {
        EnsureDeployment();
        await _mutex.WaitAsync(ct);
        try
        {
            var deployment = await LoadAsync(deploymentId, ct);
            EnsureStaging(deployment);
            ValidateConfirmation(deployment, confirmationToken, "apply");
            var fresh = await BuildPreviewAsync(deployment, ct);
            if (deployment.Preview is null || !string.Equals(fresh.Fingerprint, deployment.Preview.Fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The deployment target changed after preview. Preview the deployment again.");
            }

            var targetRoot = DevelopmentPathPolicy.TargetRoot(deployment.Kind, deployment.PackageName);
            if (deployment.Kind == "custom_card")
            {
                var baseUrl = $"/local/{deployment.PackageName}/{deployment.EntryFile}";
                var previousResource = (await ListResourcesAsync(ct)).FirstOrDefault(resource => StripQuery(resource.Url) == baseUrl);
                deployment.PreviousResourceId = previousResource?.Id;
                deployment.PreviousResourceUrl = previousResource?.Url;
                deployment.PreviousResourceType = previousResource?.Type;
                deployment.ResourceCreated = previousResource is null;
            }
            var backupRoot = BackupRoot(deployment);
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, recursive: true);
            Directory.CreateDirectory(backupRoot);
            foreach (var current in fresh.CurrentHashes.Keys)
            {
                var content = await _transport.ReadFileAsync($"{targetRoot}/{current}", ct);
                var backupPath = Path.Combine(backupRoot, current.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                await File.WriteAllBytesAsync(backupPath, content, ct);
            }

            try
            {
                foreach (var file in deployment.Files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
                {
                    var content = await File.ReadAllBytesAsync(StagePath(deployment, file.RelativePath), ct);
                    await _transport.WriteFileAtomicAsync($"{targetRoot}/{file.RelativePath}", content, ct);
                }
                foreach (var file in deployment.Files.Values)
                {
                    var written = await _transport.ReadFileAsync($"{targetRoot}/{file.RelativePath}", ct);
                    if (!string.Equals(Sha(written), file.Sha256, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Hash verification failed after writing {file.RelativePath}.");
                }
                if (deployment.RemoveMissingFiles)
                {
                    foreach (var missing in fresh.CurrentHashes.Keys.Except(fresh.ProposedHashes.Keys, StringComparer.Ordinal))
                    {
                        await _transport.DeleteFileAsync($"{targetRoot}/{missing}", ct);
                    }
                }

                deployment.AppliedHashes = deployment.RemoveMissingFiles
                    ? new Dictionary<string, string>(fresh.ProposedHashes, StringComparer.Ordinal)
                    : fresh.CurrentHashes.Concat(fresh.ProposedHashes)
                        .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
                deployment.AppliedUtc = DateTimeOffset.UtcNow;
                deployment.State = "applied";
                deployment.ConfirmationHash = null;
                if (deployment.Kind == "custom_card")
                {
                    deployment.ResourceUrl = await RegisterCardResourceAsync(deployment, ct);
                }
                else
                {
                    deployment.ConfigCheck = await _homeAssistant.PostJsonAsync("api/config/core/check_config", new { }, ct);
                }
            }
            catch
            {
                await RestoreSnapshotAsync(targetRoot, fresh.CurrentHashes, backupRoot, fresh.ProposedHashes.Keys, ct);
                if (deployment.Kind == "custom_card") await RestoreCardResourceAsync(deployment, ct);
                throw;
            }
            await SaveUnlockedAsync(deployment, ct);
            return PublicDeployment(deployment);
        }
        catch (Exception ex)
        {
            await TryRecordErrorAsync(deploymentId, ex.Message, ct);
            throw;
        }
        finally { _mutex.Release(); }
    }

    public async Task<object> VerifyDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        EnsureInspection();
        var deployment = await LoadAsync(deploymentId, ct);
        if (deployment.State is not ("applied" or "verified")) throw new InvalidOperationException("Only applied deployments can be verified.");
        object result;
        if (deployment.Kind == "custom_card")
        {
            if (string.IsNullOrWhiteSpace(deployment.ResourceUrl) || string.IsNullOrWhiteSpace(deployment.EntryFile))
                throw new InvalidOperationException("Card deployment has no registered resource URL.");
            var bytes = await _homeAssistant.GetBytesAsync(
                deployment.ResourceUrl.TrimStart('/'),
                Clamp(_options.MaxFileBytes, 4096, 128 * 1024 * 1024),
                ct);
            var hash = Sha(bytes);
            var expected = deployment.AppliedHashes[deployment.EntryFile];
            if (!string.Equals(hash, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Home Assistant did not serve the deployed custom-card bytes.");
            result = new { resourceUrl = deployment.ResourceUrl, sha256 = hash, served = true };
        }
        else
        {
            var entries = await _webSocket.SendCommandAsync(new Dictionary<string, object?> { ["type"] = "config_entries/get" }, ct);
            var matches = entries.ValueKind == JsonValueKind.Array
                ? entries.EnumerateArray().Where(item => item.TryGetProperty("domain", out var domain) && domain.GetString() == deployment.PackageName).Select(item => item.Clone()).ToArray()
                : [];
            var logs = await GetDomainLogsAsync(deployment.PackageName, ct);
            result = new { configEntries = matches, logs };
        }

        deployment.VerifiedUtc = DateTimeOffset.UtcNow;
        deployment.State = "verified";
        await SaveAsync(deployment, ct);
        return new { deployment = PublicDeployment(deployment), verification = result };
    }

    public async Task<object> PreviewRollbackAsync(string deploymentId, CancellationToken ct)
    {
        EnsureDeployment();
        var deployment = await LoadAsync(deploymentId, ct);
        if (deployment.State is not ("applied" or "verified")) throw new InvalidOperationException("Only applied deployments can be rolled back.");
        if (!Directory.Exists(BackupRoot(deployment))) throw new InvalidOperationException("This deployment has no rollback snapshot.");
        await EnsureAppliedTargetUnchangedAsync(deployment, ct);
        var confirmation = IssueConfirmation(deployment, "rollback");
        await SaveAsync(deployment, ct);
        return new { deployment.Id, action = "rollback", files = deployment.AppliedHashes.Keys.OrderBy(path => path).ToArray(), confirmation };
    }

    public async Task<object> RollbackAsync(string deploymentId, string confirmationToken, CancellationToken ct)
    {
        EnsureDeployment();
        await _mutex.WaitAsync(ct);
        try
        {
            var deployment = await LoadAsync(deploymentId, ct);
            ValidateConfirmation(deployment, confirmationToken, "rollback");
            await EnsureAppliedTargetUnchangedAsync(deployment, ct);
            var targetRoot = DevelopmentPathPolicy.TargetRoot(deployment.Kind, deployment.PackageName);
            foreach (var current in deployment.AppliedHashes.Keys)
            {
                await _transport.DeleteFileAsync($"{targetRoot}/{current}", ct);
            }
            foreach (var backup in Directory.EnumerateFiles(BackupRoot(deployment), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(BackupRoot(deployment), backup).Replace('\\', '/');
                await _transport.WriteFileAtomicAsync($"{targetRoot}/{relative}", await File.ReadAllBytesAsync(backup, ct), ct);
            }
            var restoredHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var backup in Directory.EnumerateFiles(BackupRoot(deployment), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(BackupRoot(deployment), backup).Replace('\\', '/');
                restoredHashes[relative] = Sha(await File.ReadAllBytesAsync(backup, ct));
            }
            deployment.AppliedHashes = restoredHashes;
            if (deployment.Kind == "custom_card")
            {
                await RestoreCardResourceAsync(deployment, ct);
                deployment.ResourceUrl = deployment.PreviousResourceUrl;
            }
            else if (deployment.Kind == "custom_integration")
            {
                deployment.ConfigCheck = await _homeAssistant.PostJsonAsync("api/config/core/check_config", new { }, ct);
            }
            deployment.State = "rolled_back";
            deployment.ConfirmationHash = null;
            await SaveUnlockedAsync(deployment, ct);
            return PublicDeployment(deployment);
        }
        finally { _mutex.Release(); }
    }

    public async Task<object> PrepareRestartAsync(string deploymentId, CancellationToken ct)
    {
        EnsureRestart();
        var deployment = await LoadAsync(deploymentId, ct);
        if (deployment.Kind != "custom_integration" || deployment.State is not ("applied" or "verified"))
            throw new InvalidOperationException("Restart confirmation requires an applied custom-integration deployment.");
        if (deployment.ConfigCheck is JsonElement check &&
            check.TryGetProperty("result", out var result) &&
            !string.Equals(result.GetString(), "valid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Home Assistant's configuration check is not valid. Roll back or correct the deployment before restarting.");
        }
        var state = await _homeAssistant.GetJsonAsync("api/core/state", ct);
        var confirmation = IssueConfirmation(deployment, "restart");
        await SaveAsync(deployment, ct);
        return new { deployment.Id, action = "restart", currentState = state, confirmation };
    }

    public async Task<object> RestartAndVerifyAsync(string deploymentId, string confirmationToken, CancellationToken ct)
    {
        EnsureRestart();
        var deployment = await LoadAsync(deploymentId, ct);
        ValidateConfirmation(deployment, confirmationToken, "restart");
        deployment.ConfirmationHash = null;
        await SaveAsync(deployment, ct);
        try
        {
            await _homeAssistant.PostJsonAsync("api/services/homeassistant/restart", new { }, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // The restart can close the connection before Home Assistant sends a response.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Clamp(_options.RestartTimeoutSeconds, 30, 900));
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await _homeAssistant.GetJsonAsync("api/core/state", ct);
                if (state.TryGetProperty("state", out var value) && value.GetString() == "RUNNING")
                {
                    return await VerifyDeploymentAsync(deploymentId, ct);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { last = ex; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        var detail = last is null ? string.Empty : $" {RedactLogLine(last.Message)}";
        throw new TimeoutException($"Home Assistant did not return to RUNNING before the restart timeout.{detail}");
    }

    public async Task<object> ListDeploymentsAsync(CancellationToken ct)
    {
        EnsureInspection();
        if (!Directory.Exists(_dataRoot)) return new { deployments = Array.Empty<object>() };
        var deployments = new List<DevelopmentDeployment>();
        foreach (var directory in Directory.EnumerateDirectories(_dataRoot))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (id.Length != 32 || id.Any(ch => !Uri.IsHexDigit(ch))) continue;
            var metadata = MetadataPath(id);
            if (!File.Exists(metadata)) continue;
            try
            {
                var deployment = JsonSerializer.Deserialize<DevelopmentDeployment>(await File.ReadAllTextAsync(metadata, ct), JsonOptions);
                if (deployment is not null && string.Equals(deployment.Id, id, StringComparison.Ordinal)) deployments.Add(deployment);
            }
            catch (JsonException) { }
        }
        return new { deployments = deployments.OrderByDescending(item => item.CreatedUtc).Select(PublicDeployment).ToArray() };
    }

    public async Task<object> GetDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        EnsureInspection();
        return PublicDeployment(await LoadAsync(deploymentId, ct));
    }

    private async Task<DevelopmentPreview> BuildPreviewAsync(DevelopmentDeployment deployment, CancellationToken ct)
    {
        await DevelopmentPackageValidator.ValidateAsync(deployment, StagingRoot(deployment), ct);
        var targetRoot = DevelopmentPathPolicy.TargetRoot(deployment.Kind, deployment.PackageName);
        var currentEntries = await SafeListAsync(targetRoot, ct);
        EnsureInventoryBounds(currentEntries);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in currentEntries)
        {
            var relative = entry.RelativePath[(targetRoot.Length + 1)..];
            current[relative] = Sha(await _transport.ReadFileAsync(entry.RelativePath, ct));
        }
        var proposed = deployment.Files.ToDictionary(pair => pair.Key, pair => pair.Value.Sha256, StringComparer.Ordinal);
        var paths = current.Keys.Union(proposed.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
        var changes = paths.Select(path =>
        {
            current.TryGetValue(path, out var before);
            proposed.TryGetValue(path, out var after);
            var change = before is null ? "add" : after is null ? (deployment.RemoveMissingFiles ? "remove" : "preserve") : before == after ? "unchanged" : "change";
            return new DevelopmentChange(path, change, before, after);
        }).ToList();
        var fingerprint = Sha(Encoding.UTF8.GetBytes(string.Join('\n', changes.Select(change => $"{change.Path}|{change.Change}|{change.CurrentSha256}|{change.ProposedSha256}"))));
        return new DevelopmentPreview
        {
            Fingerprint = fingerprint,
            CreatedUtc = DateTimeOffset.UtcNow,
            Changes = changes,
            CurrentHashes = current,
            ProposedHashes = proposed,
            ProposedBytes = deployment.Files.Values.Sum(file => file.Length),
            RestartRequired = deployment.Kind == "custom_integration",
        };
    }

    private async Task<string> RegisterCardResourceAsync(DevelopmentDeployment deployment, CancellationToken ct)
    {
        var hash = deployment.AppliedHashes[deployment.EntryFile!][..12];
        var baseUrl = $"/local/{deployment.PackageName}/{deployment.EntryFile}";
        var url = $"{baseUrl}?v={hash}";
        var resources = await ListResourcesAsync(ct);
        var existing = resources.FirstOrDefault(resource => StripQuery(resource.Url) == baseUrl);
        if (existing is null)
        {
            await _webSocket.SendCommandAsync(new Dictionary<string, object?>
            {
                ["type"] = "lovelace/resources/create",
                ["url"] = url,
                ["res_type"] = "module",
            }, ct);
        }
        else
        {
            await _webSocket.SendCommandAsync(new Dictionary<string, object?>
            {
                ["type"] = "lovelace/resources/update",
                ["resource_id"] = existing.Id,
                ["url"] = url,
                ["res_type"] = "module",
            }, ct);
        }
        return url;
    }

    private async Task<LovelaceResource[]> ListResourcesAsync(CancellationToken ct)
    {
        try
        {
            var result = await _webSocket.SendCommandAsync(new Dictionary<string, object?> { ["type"] = "lovelace/resources/list" }, ct);
            if (result.ValueKind != JsonValueKind.Array) return [];
            return result.EnumerateArray().Select(item => new LovelaceResource(
                item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty :
                item.TryGetProperty("res_type", out var resourceType) ? resourceType.GetString() ?? string.Empty : string.Empty)).ToArray();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown_command", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _webSocket.SendCommandAsync(new Dictionary<string, object?> { ["type"] = "lovelace/resources" }, ct);
            return result.ValueKind == JsonValueKind.Array
                ? result.EnumerateArray().Select(item => new LovelaceResource(
                    item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty)).ToArray()
                : [];
        }
    }

    private async Task<object> DescribeIntegrationAsync(string domain, IReadOnlyList<ConfigFileEntry> files, bool includeLogs, CancellationToken ct)
    {
        JsonElement? manifest = null;
        var manifestEntry = files.FirstOrDefault(file => file.RelativePath.EndsWith("/manifest.json", StringComparison.Ordinal));
        if (manifestEntry is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(await _transport.ReadFileAsync(manifestEntry.RelativePath, ct));
                manifest = document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        JsonElement[] entries = [];
        try
        {
            var allEntries = await _webSocket.SendCommandAsync(new Dictionary<string, object?> { ["type"] = "config_entries/get" }, ct);
            if (allEntries.ValueKind == JsonValueKind.Array)
                entries = allEntries.EnumerateArray().Where(item => item.TryGetProperty("domain", out var value) && value.GetString() == domain).Select(item => item.Clone()).ToArray();
        }
        catch (InvalidOperationException) { }
        var described = await DescribeFilesAsync(DevelopmentPathPolicy.TargetRoot("custom_integration", domain), ct);
        return new { domain, manifest, files = described, configEntries = entries, logs = includeLogs ? await GetDomainLogsAsync(domain, ct) : null };
    }

    private async Task<object[]> DescribeFilesAsync(string root, CancellationToken ct)
    {
        var entries = await SafeListAsync(root, ct);
        EnsureInventoryBounds(entries);
        var output = new List<object>();
        foreach (var entry in entries)
        {
            var content = await _transport.ReadFileAsync(entry.RelativePath, ct);
            output.Add(new { path = entry.RelativePath[(root.Length + 1)..], entry.Length, sha256 = Sha(content), entry.ModifiedUtc });
        }
        return output.ToArray();
    }

    private async Task<string?> GetDomainLogsAsync(string domain, CancellationToken ct)
    {
        try
        {
            var max = Clamp(_options.MaxLogCharacters, 1024, 262144);
            var bytes = await _homeAssistant.GetBytesAsync("api/error_log", Math.Min(max * 4, 1024 * 1024), ct);
            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(domain, StringComparison.OrdinalIgnoreCase))
                .Select(RedactLogLine);
            var result = string.Join(Environment.NewLine, lines);
            return result.Length <= max ? result : result[^max..];
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return $"Log retrieval failed: {RedactLogLine(ex.Message)}";
        }
    }

    private async Task EnsureAppliedTargetUnchangedAsync(DevelopmentDeployment deployment, CancellationToken ct)
    {
        var root = DevelopmentPathPolicy.TargetRoot(deployment.Kind, deployment.PackageName);
        var entries = await SafeListAsync(root, ct);
        EnsureInventoryBounds(entries);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var relative = entry.RelativePath[(root.Length + 1)..];
            current[relative] = Sha(await _transport.ReadFileAsync(entry.RelativePath, ct));
        }
        if (current.Count != deployment.AppliedHashes.Count || current.Any(pair => !deployment.AppliedHashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new InvalidOperationException("The deployed target changed after apply. Rollback was refused to preserve those changes.");
    }

    private async Task RestoreSnapshotAsync(
        string targetRoot,
        IReadOnlyDictionary<string, string> previousHashes,
        string backupRoot,
        IEnumerable<string> proposedPaths,
        CancellationToken ct)
    {
        foreach (var path in proposedPaths.Except(previousHashes.Keys, StringComparer.Ordinal))
        {
            await _transport.DeleteFileAsync($"{targetRoot}/{path}", ct);
        }
        foreach (var previous in previousHashes.Keys)
        {
            var backup = Path.Combine(backupRoot, previous.Replace('/', Path.DirectorySeparatorChar));
            await _transport.WriteFileAtomicAsync($"{targetRoot}/{previous}", await File.ReadAllBytesAsync(backup, ct), ct);
        }
    }

    private DevelopmentConfirmation IssueConfirmation(DevelopmentDeployment deployment, string action)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddMinutes(Clamp(_options.ConfirmationLifetimeMinutes, 1, 60));
        deployment.ConfirmationHash = Sha(Encoding.UTF8.GetBytes(token));
        deployment.ConfirmationExpiresUtc = expires;
        deployment.ConfirmationAction = action;
        return new DevelopmentConfirmation(token, expires);
    }

    private static void ValidateConfirmation(DevelopmentDeployment deployment, string token, string action)
    {
        if (deployment.ConfirmationExpiresUtc < DateTimeOffset.UtcNow || deployment.ConfirmationAction != action || string.IsNullOrWhiteSpace(deployment.ConfirmationHash))
            throw new InvalidOperationException("The confirmation token is missing, expired, or bound to another action.");
        if (token.Length != 64 || token.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException("The confirmation token is invalid.");
        var actual = Convert.FromHexString(Sha(Encoding.UTF8.GetBytes(token)));
        var expected = Convert.FromHexString(deployment.ConfirmationHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidOperationException("The confirmation token is invalid.");
    }

    private async Task<IReadOnlyList<ConfigFileEntry>> SafeListAsync(string root, CancellationToken ct)
    {
        if (!_transport.IsConfigured) return [];
        return await _transport.ListFilesAsync(root, ct);
    }

    private async Task<DevelopmentDeployment> LoadAsync(string id, CancellationToken ct)
    {
        if (id.Length != 32 || id.Any(ch => !Uri.IsHexDigit(ch))) throw new ArgumentException("Invalid deployment ID.", nameof(id));
        var path = MetadataPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException("Deployment was not found.");
        var deployment = JsonSerializer.Deserialize<DevelopmentDeployment>(await File.ReadAllTextAsync(path, ct), JsonOptions)
            ?? throw new InvalidOperationException("Deployment metadata is invalid.");
        if (!string.Equals(deployment.Id, id, StringComparison.Ordinal))
            throw new InvalidOperationException("Deployment metadata does not match its storage directory.");
        return deployment;
    }

    private async Task SaveAsync(DevelopmentDeployment deployment, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try { await SaveUnlockedAsync(deployment, ct); }
        finally { _mutex.Release(); }
    }

    private async Task SaveUnlockedAsync(DevelopmentDeployment deployment, CancellationToken ct)
    {
        Directory.CreateDirectory(DeploymentRoot(deployment.Id));
        var path = MetadataPath(deployment.Id);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(deployment, JsonOptions), new UTF8Encoding(false), ct);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task TryRecordErrorAsync(string id, string error, CancellationToken ct)
    {
        try
        {
            var deployment = await LoadAsync(id, ct);
            var redacted = RedactLogLine(error);
            deployment.LastError = redacted.Length > 2048 ? redacted[..2048] : redacted;
            deployment.State = "failed";
            await SaveUnlockedAsync(deployment, ct);
        }
        catch { }
    }

    private void EnsureInspection()
    {
        if (!_options.EnableInspection) throw new InvalidOperationException("Development inspection tools are disabled.");
    }

    private void EnsureDeployment()
    {
        EnsureInspection();
        if (!_options.EnableDeployment) throw new InvalidOperationException("Development deployment tools are disabled.");
        _homeAssistant.EnsureWriteAllowed("Home Assistant development deployment");
        if (!_transport.IsConfigured) throw new InvalidOperationException("A Local or SFTP development transport must be configured.");
    }

    private void EnsureRestart()
    {
        EnsureDeployment();
        if (!_options.EnableRestart) throw new InvalidOperationException("Development restart tools are disabled.");
    }

    private static void EnsureStaging(DevelopmentDeployment deployment)
    {
        if (deployment.State != "staging") throw new InvalidOperationException("The deployment is no longer in staging state.");
        if (deployment.ExpiresUtc < DateTimeOffset.UtcNow) throw new InvalidOperationException("The deployment session expired.");
    }

    private string DeploymentRoot(string id) => Path.Combine(_dataRoot, id);
    private string MetadataPath(string id) => Path.Combine(DeploymentRoot(id), "deployment.json");
    private string StagingRoot(DevelopmentDeployment deployment) => Path.Combine(DeploymentRoot(deployment.Id), "staging");
    private string BackupRoot(DevelopmentDeployment deployment) => Path.Combine(DeploymentRoot(deployment.Id), "backup");
    private string StagePath(DevelopmentDeployment deployment, string relative) => Path.Combine(StagingRoot(deployment), relative.Replace('/', Path.DirectorySeparatorChar));
    private static string NormalizeSha(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch))) throw new ArgumentException("SHA-256 values must contain 64 hexadecimal characters.");
        return normalized;
    }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
    private static string? FirstSegment(string path, string root)
    {
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return null;
        return path[(root.Length + 1)..].Split('/')[0];
    }
    private static string StripQuery(string url) => url.Split('?', 2)[0];
    private static bool ResourceMatchesPackage(string url, string package) => StripQuery(url).StartsWith($"/local/{package}/", StringComparison.OrdinalIgnoreCase);
    private static object PublicDeployment(DevelopmentDeployment deployment) => new
    {
        deployment.Id,
        deployment.Kind,
        deployment.PackageName,
        deployment.EntryFile,
        deployment.RemoveMissingFiles,
        deployment.State,
        deployment.CreatedUtc,
        deployment.ExpiresUtc,
        deployment.AppliedUtc,
        deployment.VerifiedUtc,
        files = deployment.Files.Values.OrderBy(file => file.RelativePath).ToArray(),
        deployment.Preview,
        deployment.ResourceUrl,
        deployment.ConfigCheck,
        deployment.LastError,
    };

    private sealed record LovelaceResource(string Id, string Url, string Type);

    private async Task RestoreCardResourceAsync(DevelopmentDeployment deployment, CancellationToken ct)
    {
        var baseUrl = $"/local/{deployment.PackageName}/{deployment.EntryFile}";
        var current = (await ListResourcesAsync(ct)).FirstOrDefault(resource => StripQuery(resource.Url) == baseUrl);
        if (deployment.ResourceCreated)
        {
            if (current is not null)
            {
                await _webSocket.SendCommandAsync(new Dictionary<string, object?>
                {
                    ["type"] = "lovelace/resources/delete",
                    ["resource_id"] = current.Id,
                }, ct);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(deployment.PreviousResourceId) && !string.IsNullOrWhiteSpace(deployment.PreviousResourceUrl))
        {
            await _webSocket.SendCommandAsync(new Dictionary<string, object?>
            {
                ["type"] = "lovelace/resources/update",
                ["resource_id"] = deployment.PreviousResourceId,
                ["url"] = deployment.PreviousResourceUrl,
                ["res_type"] = string.IsNullOrWhiteSpace(deployment.PreviousResourceType) ? "module" : deployment.PreviousResourceType,
            }, ct);
        }
    }

    private static string? GetJsonProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static string RedactLogLine(string line)
    {
        var redacted = SensitiveJsonValue.Replace(line, "$1<redacted>");
        redacted = SensitiveAssignedValue.Replace(redacted, match =>
        {
            var separator = match.Value.IndexOfAny(['=', ':']);
            return separator < 0 ? "<redacted>" : match.Value[..(separator + 1)] + "<redacted>";
        });
        return BearerValue.Replace(redacted, "Bearer <redacted>");
    }

    private void EnsureInventoryBounds(IReadOnlyList<ConfigFileEntry> entries)
    {
        if (entries.Count > 1000) throw new InvalidOperationException("Development target contains more than 1000 files.");
        var maxFile = Clamp(_options.MaxFileBytes, 4096, 128 * 1024 * 1024);
        if (entries.Any(entry => entry.Length > maxFile))
            throw new InvalidOperationException("A development target file exceeds Development:MaxFileBytes.");
        var maxPackage = Clamp(_options.MaxPackageBytes, 4096, 512 * 1024 * 1024);
        if (entries.Sum(entry => entry.Length) > maxPackage)
            throw new InvalidOperationException("Development target exceeds Development:MaxPackageBytes.");
    }
}
