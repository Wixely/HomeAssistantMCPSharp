using System.Text.RegularExpressions;

namespace HomeAssistantMCPSharp.Services;

internal static partial class DevelopmentPathPolicy
{
    private static readonly HashSet<string> CardExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".map", ".json", ".png", ".svg", ".jpg", ".jpeg", ".webp", ".woff", ".woff2",
    };

    private static readonly HashSet<string> IntegrationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".json", ".yaml", ".yml", ".md", ".txt", ".po", ".mo", ".png", ".svg", ".jpg", ".jpeg", ".webp",
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegrationNameRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CardNameRegex();

    public static string NormalizeKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "custom_card" or "card" => "custom_card",
        "custom_integration" or "integration" => "custom_integration",
        _ => throw new ArgumentException("kind must be custom_card or custom_integration.", nameof(kind)),
    };

    public static string ValidatePackageName(string kind, string packageName)
    {
        var normalized = packageName.Trim().ToLowerInvariant();
        var regex = kind == "custom_integration" ? IntegrationNameRegex() : CardNameRegex();
        if (!regex.IsMatch(normalized))
        {
            throw new ArgumentException(
                kind == "custom_integration"
                    ? "Integration package names must start with a lowercase letter and contain only lowercase letters, digits, and underscores."
                    : "Card package names must contain only lowercase letters, digits, hyphens, and underscores.",
                nameof(packageName));
        }

        return normalized;
    }

    public static string NormalizeRelativeFile(string kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A relative file path is required.", nameof(path));
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Length > 240 || Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new ArgumentException("The deployment file path is invalid.", nameof(path));
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Length > 100))
        {
            throw new ArgumentException("The deployment file path contains an invalid segment.", nameof(path));
        }

        var extension = Path.GetExtension(normalized);
        var allowed = kind == "custom_integration" ? IntegrationExtensions : CardExtensions;
        if (!allowed.Contains(extension))
        {
            throw new ArgumentException($"Files with extension '{extension}' are not allowed for {kind} deployments.", nameof(path));
        }

        return string.Join('/', segments);
    }

    public static string TargetRoot(string kind, string packageName) =>
        kind == "custom_integration"
            ? $"custom_components/{packageName}"
            : $"www/{packageName}";
}
