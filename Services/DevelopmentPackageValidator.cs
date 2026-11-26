using System.Text.Json;

namespace HomeAssistantMCPSharp.Services;

internal static class DevelopmentPackageValidator
{
    public static async Task ValidateAsync(
        DevelopmentDeployment deployment,
        string stagingRoot,
        CancellationToken ct)
    {
        if (deployment.Files.Count == 0 || deployment.Files.Values.Any(file => !file.Complete))
        {
            throw new InvalidOperationException("Every deployment file must be uploaded and finalized before preview.");
        }

        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in deployment.Files.Values)
        {
            if (!caseInsensitive.Add(file.RelativePath))
            {
                throw new InvalidOperationException("Deployment file names must not collide when compared without case.");
            }
        }

        if (deployment.Kind == "custom_card")
        {
            if (string.IsNullOrWhiteSpace(deployment.EntryFile))
            {
                throw new InvalidOperationException("Custom-card deployments require an entryFile.");
            }
            if (!deployment.Files.ContainsKey(deployment.EntryFile))
            {
                throw new InvalidOperationException("The custom-card entryFile is not present in the staged package.");
            }
            if (Path.GetExtension(deployment.EntryFile) is not (".js" or ".mjs"))
            {
                throw new InvalidOperationException("The custom-card entryFile must be a .js or .mjs module.");
            }
            return;
        }

        if (!deployment.Files.ContainsKey("manifest.json"))
        {
            throw new InvalidOperationException("Custom integrations require manifest.json at the package root.");
        }
        if (!deployment.Files.ContainsKey("__init__.py"))
        {
            throw new InvalidOperationException("Custom integrations require __init__.py at the package root.");
        }

        var manifestPath = Path.Combine(stagingRoot, "manifest.json");
        await using var manifestStream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var root = document.RootElement;
        RequireValue(root, "domain");
        RequireValue(root, "name");
        RequireValue(root, "version");
        RequireValue(root, "documentation");
        RequireValue(root, "codeowners");
        if (!string.Equals(root.GetProperty("domain").GetString(), deployment.PackageName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("manifest.json domain must match the deployment package name.");
        }
        if (root.TryGetProperty("requirements", out var requirements) &&
            (requirements.ValueKind != JsonValueKind.Array || requirements.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
        {
            throw new InvalidOperationException("manifest.json requirements must be an array of strings.");
        }

        foreach (var json in deployment.Files.Values.Where(file => Path.GetExtension(file.RelativePath).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            await using var stream = File.OpenRead(Path.Combine(stagingRoot, json.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            try { using var _ = await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
            catch (JsonException ex) { throw new InvalidOperationException($"{json.RelativePath} is not valid JSON: {ex.Message}"); }
        }
    }

    private static void RequireValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            (name == "codeowners"
                ? value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                : value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
        {
            throw new InvalidOperationException($"manifest.json requires a non-empty {name} value.");
        }
    }
}
