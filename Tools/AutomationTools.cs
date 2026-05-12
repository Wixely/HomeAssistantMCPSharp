using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Convenience wrappers around the automation, script, and scene domains.
/// All reads come from /api/states; all writes go via POST /api/services/&lt;domain&gt;/&lt;service&gt;.
/// </summary>
[McpServerToolType]
public static class AutomationTools
{
    [McpServerTool(Name = "ha_list_automations"),
     Description("List every automation entity with its on/off state, friendly name, and last triggered timestamp.")]
    public static Task<string> ListAutomations(HomeAssistantService svc, CancellationToken ct = default)
        => ListDomainAsync(svc, "automation", extraAttr: "last_triggered", ct);

    [McpServerTool(Name = "ha_list_scripts"),
     Description("List every script entity with its current state and friendly name.")]
    public static Task<string> ListScripts(HomeAssistantService svc, CancellationToken ct = default)
        => ListDomainAsync(svc, "script", extraAttr: null, ct);

    [McpServerTool(Name = "ha_list_scenes"),
     Description("List every scene entity with its friendly name.")]
    public static Task<string> ListScenes(HomeAssistantService svc, CancellationToken ct = default)
        => ListDomainAsync(svc, "scene", extraAttr: null, ct);

    [McpServerTool(Name = "ha_enable_automation"),
     Description("Enable an automation. Calls automation.turn_on. Requires write mode.")]
    public static Task<string> EnableAutomation(
        HomeAssistantService svc,
        [Description("Automation entity id, e.g. 'automation.morning_lights'.")] string entityId,
        CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_enable_automation", "automation", "turn_on", entityId, null, ct);

    [McpServerTool(Name = "ha_disable_automation"),
     Description("Disable an automation. Calls automation.turn_off. Requires write mode.")]
    public static Task<string> DisableAutomation(
        HomeAssistantService svc,
        [Description("Automation entity id.")] string entityId,
        CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_disable_automation", "automation", "turn_off", entityId, null, ct);

    [McpServerTool(Name = "ha_trigger_automation"),
     Description("Run an automation now. Calls automation.trigger. Set skipConditions=true to bypass the automation's conditions. Requires write mode.")]
    public static Task<string> TriggerAutomation(
        HomeAssistantService svc,
        [Description("Automation entity id.")] string entityId,
        [Description("When true, the automation's conditions are skipped. Defaults to false.")] bool skipConditions = false,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["skip_condition"] = skipConditions };
        return CallEntityServiceAsync(svc, "ha_trigger_automation", "automation", "trigger", entityId, body, ct);
    }

    [McpServerTool(Name = "ha_run_script"),
     Description("Run a script. Calls script.turn_on. Provide optional variables JSON to pass into the script. Requires write mode.")]
    public static async Task<string> RunScript(
        HomeAssistantService svc,
        [Description("Script entity id, e.g. 'script.bedtime'.")] string entityId,
        [Description("Optional JSON object of variables to pass into the script.")] string? variablesJson = null,
        CancellationToken ct = default)
    {
        EnsureWriteAndServices(svc, "ha_run_script");
        svc.EnsureEntityAllowed(entityId);

        var body = new Dictionary<string, object?> { ["entity_id"] = entityId };
        if (!string.IsNullOrWhiteSpace(variablesJson))
        {
            using var doc = JsonDocument.Parse(variablesJson);
            body["variables"] = doc.RootElement.Clone();
        }
        var result = await svc.PostJsonAsync("api/services/script/turn_on", body, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_activate_scene"),
     Description("Activate a scene. Calls scene.turn_on. Requires write mode.")]
    public static Task<string> ActivateScene(
        HomeAssistantService svc,
        [Description("Scene entity id, e.g. 'scene.movie_night'.")] string entityId,
        CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_activate_scene", "scene", "turn_on", entityId, null, ct);

    // ---- helpers -------------------------------------------------------

    private static async Task<string> ListDomainAsync(HomeAssistantService svc, string domain, string? extraAttr, CancellationToken ct)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        var prefix = domain + ".";
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var id)) continue;
            var entityId = id.GetString();
            if (entityId is null || !entityId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null;
            string? extra = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                    friendly = fn.GetString();
                if (extraAttr is not null && attrs.TryGetProperty(extraAttr, out var ex) && ex.ValueKind == JsonValueKind.String)
                    extra = ex.GetString();
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            rows.Add(extraAttr is null
                ? new { entity_id = entityId, friendly_name = friendly, state }
                : (object)new { entity_id = entityId, friendly_name = friendly, state, last_triggered = extra });
        }
        return JsonOpts.Serialize(rows);
    }

    private static async Task<string> CallEntityServiceAsync(
        HomeAssistantService svc,
        string toolName,
        string domain,
        string service,
        string entityId,
        Dictionary<string, object?>? body,
        CancellationToken ct)
    {
        EnsureWriteAndServices(svc, toolName);
        svc.EnsureEntityAllowed(entityId);

        body ??= new Dictionary<string, object?>();
        body["entity_id"] = entityId;
        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }

    private static void EnsureWriteAndServices(HomeAssistantService svc, string toolName)
    {
        if (!svc.Options.EnableShortcuts) throw new InvalidOperationException("Shortcut tools are disabled.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed(toolName);
    }
}
