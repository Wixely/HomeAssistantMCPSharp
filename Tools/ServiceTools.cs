using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/services and POST /api/services/&lt;domain&gt;/&lt;service&gt; — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class ServiceTools
{
    [McpServerTool(Name = "ha_list_services"),
     Description("List every service registered with Home Assistant, grouped by domain. Calls GET /api/services.")]
    public static async Task<string> ListServices(
        HomeAssistantService svc,
        [Description("Optional domain filter, e.g. 'light'.")] string? domain = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        var json = await svc.GetJsonAsync("api/services", ct);
        if (string.IsNullOrWhiteSpace(domain) || json.ValueKind != JsonValueKind.Array)
            return JsonOpts.Serialize(json);

        var filtered = json.EnumerateArray()
            .Where(el => el.TryGetProperty("domain", out var d)
                         && string.Equals(d.GetString(), domain, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return JsonOpts.Serialize(filtered);
    }

    [McpServerTool(Name = "ha_call_service"),
     Description("Invoke a Home Assistant service to actuate devices, run scripts, etc. " +
                 "Calls POST /api/services/<domain>/<service>. Requires write mode. " +
                 "Use ha_list_services to discover available services and their fields.")]
    public static async Task<string> CallService(
        HomeAssistantService svc,
        [Description("Service domain, e.g. 'light', 'switch', 'homeassistant', 'script'.")] string domain,
        [Description("Service name within that domain, e.g. 'turn_on', 'toggle', 'reload'.")] string service,
        [Description("Optional target entity_id (single value).")] string? entityId = null,
        [Description("Optional service-data JSON object, e.g. '{\"brightness_pct\":50}'.")] string? dataJson = null,
        [Description("Optional target JSON object, e.g. '{\"area_id\":\"kitchen\"}'.")] string? targetJson = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed("ha_call_service");
        svc.EnsureDomainAllowed(domain);
        if (!string.IsNullOrWhiteSpace(entityId)) svc.EnsureEntityAllowed(entityId);

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(entityId)) body["entity_id"] = entityId;

        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            using var doc = JsonDocument.Parse(dataJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
                body[prop.Name] = prop.Value.Clone();
        }
        if (!string.IsNullOrWhiteSpace(targetJson))
        {
            using var doc = JsonDocument.Parse(targetJson);
            body["target"] = doc.RootElement.Clone();
        }

        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_check_config"),
     Description("Trigger Home Assistant to validate its configuration. Calls POST /api/config/core/check_config.")]
    public static async Task<string> CheckConfig(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed("ha_check_config");
        var result = await svc.PostJsonAsync("api/config/core/check_config", null, ct);
        return JsonOpts.Serialize(result);
    }
}
