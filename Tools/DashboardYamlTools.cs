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
    [Description("Read the default Home Assistant Lovelace dashboard config via WebSocket and expose it as YAML.")]
    public static async Task<ReadResourceResult> DashboardYamlResource(
        DashboardYamlService dashboardYaml,
        CancellationToken ct = default)
    {
        var snapshot = await dashboardYaml.ReadAsync(ct: ct);
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
     Description("Return metadata for a Home Assistant Lovelace dashboard config exposed as YAML, including SHA-256.")]
    public static async Task<string> GetDashboardYamlInfo(
        DashboardYamlService dashboardYaml,
        [Description("Optional Lovelace dashboard url_path. Omit for the default dashboard.")] string? urlPath = null,
        CancellationToken ct = default)
    {
        var info = await dashboardYaml.GetInfoAsync(urlPath, ct);
        return JsonOpts.Serialize(info);
    }

    [McpServerTool(Name = "ha_list_dashboards"),
     Description("List Home Assistant Lovelace dashboards so clients can choose the urlPath to pass to dashboard YAML tools. Uses HA's WebSocket API.")]
    public static async Task<string> ListDashboards(
        DashboardYamlService dashboardYaml,
        CancellationToken ct = default)
    {
        var dashboards = await dashboardYaml.ListDashboardsAsync(ct);
        return JsonOpts.Serialize(new
        {
            defaultDashboard = new
            {
                urlPath = (string?)null,
                note = "Omit urlPath for the default Lovelace dashboard.",
            },
            dashboards,
        });
    }

    [McpServerTool(Name = "ha_get_dashboard_yaml"),
     Description("Read a Home Assistant Lovelace dashboard config via WebSocket and return it as YAML plus a SHA-256 for safe edits.")]
    public static async Task<string> GetDashboardYaml(
        DashboardYamlService dashboardYaml,
        [Description("Optional Lovelace dashboard url_path. Omit for the default dashboard.")] string? urlPath = null,
        [Description("Force Home Assistant to reload the dashboard config instead of using a cached copy.")] bool force = false,
        CancellationToken ct = default)
    {
        var snapshot = await dashboardYaml.ReadAsync(urlPath, force, ct);
        return JsonOpts.Serialize(new
        {
            snapshot.UrlPath,
            snapshot.Content,
            snapshot.Sha256,
        });
    }

    [McpServerTool(Name = "ha_update_dashboard_yaml"),
     Description("Replace a Home Assistant Lovelace dashboard config by sending YAML through HA's WebSocket API. Requires write mode.")]
    public static async Task<string> UpdateDashboardYaml(
        DashboardYamlService dashboardYaml,
        [Description("Full replacement YAML content to write.")] string content,
        [Description("Optional Lovelace dashboard url_path. Omit for the default dashboard.")] string? urlPath = null,
        [Description("Optional SHA-256 returned by ha_get_dashboard_yaml; write is rejected if the file changed.")] string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        var result = await dashboardYaml.WriteAsync(content, urlPath, expectedSha256, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_replace_dashboard_yaml_text"),
     Description("Replace text in a Home Assistant Lovelace dashboard YAML view, then save through HA's WebSocket API. Requires write mode.")]
    public static async Task<string> ReplaceDashboardYamlText(
        DashboardYamlService dashboardYaml,
        [Description("Exact text to replace.")] string oldText,
        [Description("Replacement text.")] string newText,
        [Description("Set true to replace every occurrence. Defaults to false, which requires a single match.")] bool replaceAll = false,
        [Description("Optional Lovelace dashboard url_path. Omit for the default dashboard.")] string? urlPath = null,
        [Description("Optional SHA-256 returned by ha_get_dashboard_yaml; write is rejected if the file changed.")] string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        var result = await dashboardYaml.ReplaceAsync(oldText, newText, replaceAll, urlPath, expectedSha256, ct);
        return JsonOpts.Serialize(result);
    }
}
