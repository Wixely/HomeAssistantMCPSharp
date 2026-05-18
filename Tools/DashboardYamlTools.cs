using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

[McpServerToolType]
[McpServerResourceType]
public static class DashboardYamlTools
{
    [McpServerResource(
        UriTemplate = DashboardYamlService.ResourceUri,
        Name = "ha_dashboard_yaml",
        Title = "Home Assistant Dashboard YAML",
        MimeType = "application/x-yaml")]
    [Description("Read the configured Home Assistant dashboard YAML file as an MCP resource.")]
    public static async Task<ReadResourceResult> DashboardYamlResource(
        DashboardYamlService dashboardYaml,
        CancellationToken ct = default)
    {
        var snapshot = await dashboardYaml.ReadAsync(ct);
        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = DashboardYamlService.ResourceUri,
                    MimeType = "application/x-yaml",
                    Text = snapshot.Content,
                },
            ],
        };
    }

    [McpServerTool(Name = "ha_get_dashboard_yaml_info"),
     Description("Return metadata for the configured Home Assistant dashboard YAML file, including path, size, mtime, and SHA-256.")]
    public static async Task<string> GetDashboardYamlInfo(
        DashboardYamlService dashboardYaml,
        CancellationToken ct = default)
    {
        var snapshot = await dashboardYaml.ReadAsync(ct);
        return JsonOpts.Serialize(new
        {
            resourceUri = DashboardYamlService.ResourceUri,
            snapshot.Path,
            snapshot.Length,
            snapshot.LastWriteTimeUtc,
            snapshot.Sha256,
        });
    }

    [McpServerTool(Name = "ha_get_dashboard_yaml"),
     Description("Read the configured Home Assistant dashboard YAML file. Returns the content plus a SHA-256 for safe edits.")]
    public static async Task<string> GetDashboardYaml(
        DashboardYamlService dashboardYaml,
        CancellationToken ct = default)
    {
        var snapshot = await dashboardYaml.ReadAsync(ct);
        return JsonOpts.Serialize(new
        {
            snapshot.Path,
            snapshot.Content,
            snapshot.Length,
            snapshot.LastWriteTimeUtc,
            snapshot.Sha256,
        });
    }

    [McpServerTool(Name = "ha_update_dashboard_yaml"),
     Description("Replace the configured Home Assistant dashboard YAML file content. Requires write mode and creates a backup by default.")]
    public static async Task<string> UpdateDashboardYaml(
        DashboardYamlService dashboardYaml,
        [Description("Full replacement YAML content to write.")] string content,
        [Description("Optional SHA-256 returned by ha_get_dashboard_yaml; write is rejected if the file changed.")] string? expectedSha256 = null,
        [Description("Create a timestamped .bak copy before writing when the file exists. Defaults to true.")] bool createBackup = true,
        CancellationToken ct = default)
    {
        var result = await dashboardYaml.WriteAsync(content, expectedSha256, createBackup, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_replace_dashboard_yaml_text"),
     Description("Replace text within the configured Home Assistant dashboard YAML file. Requires write mode and creates a backup by default.")]
    public static async Task<string> ReplaceDashboardYamlText(
        DashboardYamlService dashboardYaml,
        [Description("Exact text to replace.")] string oldText,
        [Description("Replacement text.")] string newText,
        [Description("Set true to replace every occurrence. Defaults to false, which requires a single match.")] bool replaceAll = false,
        [Description("Optional SHA-256 returned by ha_get_dashboard_yaml; write is rejected if the file changed.")] string? expectedSha256 = null,
        [Description("Create a timestamped .bak copy before writing. Defaults to true.")] bool createBackup = true,
        CancellationToken ct = default)
    {
        var result = await dashboardYaml.ReplaceAsync(oldText, newText, replaceAll, expectedSha256, createBackup, ct);
        return JsonOpts.Serialize(result);
    }
}
