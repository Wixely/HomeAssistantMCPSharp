using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Typed convenience wrappers over POST /api/services/&lt;domain&gt;/&lt;service&gt;.
/// Functionally equivalent to ha_call_service but with named, documented parameters
/// so the agent doesn't have to craft service-data JSON by hand.
/// </summary>
[McpServerToolType]
public static class ShortcutTools
{
    // ---- generic turn_on / turn_off / toggle -----------------------------

    [McpServerTool(Name = "ha_turn_on"),
     Description("Turn an entity ON. Calls homeassistant.turn_on — works across light/switch/fan/cover/scene/etc. Requires write mode.")]
    public static Task<string> TurnOn(
        HomeAssistantService svc,
        [Description("Entity id, e.g. 'light.kitchen' or 'switch.lamp'.")] string entityId,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_turn_on", "homeassistant", "turn_on", entityId, body: null, ct);

    [McpServerTool(Name = "ha_turn_off"),
     Description("Turn an entity OFF. Calls homeassistant.turn_off. Requires write mode.")]
    public static Task<string> TurnOff(
        HomeAssistantService svc,
        [Description("Entity id.")] string entityId,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_turn_off", "homeassistant", "turn_off", entityId, body: null, ct);

    [McpServerTool(Name = "ha_toggle"),
     Description("Toggle an entity's state. Calls homeassistant.toggle. Requires write mode.")]
    public static Task<string> Toggle(
        HomeAssistantService svc,
        [Description("Entity id.")] string entityId,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_toggle", "homeassistant", "toggle", entityId, body: null, ct);

    // ---- light ----------------------------------------------------------

    [McpServerTool(Name = "ha_set_light"),
     Description("Set a light's brightness, color temperature, or RGB color. Calls light.turn_on. Any null/omitted parameter is left unchanged. Requires write mode.")]
    public static Task<string> SetLight(
        HomeAssistantService svc,
        [Description("Light entity id, e.g. 'light.kitchen'.")] string entityId,
        [Description("Brightness percentage 0-100. 0 turns the light off.")] int? brightnessPct = null,
        [Description("Mired color temperature, e.g. 250. Mutually exclusive with rgb.")] int? colorTempMired = null,
        [Description("Color temperature in Kelvin, e.g. 4000. Mutually exclusive with colorTempMired/rgb.")] int? colorTempKelvin = null,
        [Description("RGB color as comma-separated ints, e.g. '255,128,0'.")] string? rgb = null,
        [Description("Transition time in seconds.")] double? transitionSeconds = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (brightnessPct.HasValue) body["brightness_pct"] = Math.Clamp(brightnessPct.Value, 0, 100);
        if (colorTempMired.HasValue) body["color_temp"] = colorTempMired.Value;
        if (colorTempKelvin.HasValue) body["color_temp_kelvin"] = colorTempKelvin.Value;
        if (!string.IsNullOrWhiteSpace(rgb))
        {
            var parts = rgb.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !parts.All(p => int.TryParse(p, out _)))
                throw new ArgumentException("rgb must be three comma-separated integers, e.g. '255,128,0'.", nameof(rgb));
            body["rgb_color"] = parts.Select(int.Parse).ToArray();
        }
        if (transitionSeconds.HasValue) body["transition"] = transitionSeconds.Value;

        // brightness_pct = 0 → use turn_off, otherwise turn_on
        var service = brightnessPct.HasValue && brightnessPct.Value == 0 ? "turn_off" : "turn_on";
        return CallAsync(svc, "ha_set_light", "light", service, entityId, body, ct);
    }

    // ---- climate --------------------------------------------------------

    [McpServerTool(Name = "ha_set_climate_temperature"),
     Description("Set a climate entity's target temperature. Calls climate.set_temperature. Requires write mode.")]
    public static Task<string> SetClimateTemperature(
        HomeAssistantService svc,
        [Description("Climate entity id, e.g. 'climate.living_room'.")] string entityId,
        [Description("Target temperature. Unit follows the entity's configured unit_of_measurement.")] double temperature,
        [Description("Optional HVAC mode to switch to at the same time, e.g. 'heat', 'cool', 'heat_cool'.")] string? hvacMode = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["temperature"] = temperature };
        if (!string.IsNullOrWhiteSpace(hvacMode)) body["hvac_mode"] = hvacMode;
        return CallAsync(svc, "ha_set_climate_temperature", "climate", "set_temperature", entityId, body, ct);
    }

    [McpServerTool(Name = "ha_set_climate_mode"),
     Description("Set a climate entity's HVAC mode. Calls climate.set_hvac_mode. Requires write mode.")]
    public static Task<string> SetClimateMode(
        HomeAssistantService svc,
        [Description("Climate entity id.")] string entityId,
        [Description("HVAC mode, e.g. 'off', 'heat', 'cool', 'heat_cool', 'auto', 'dry', 'fan_only'.")] string hvacMode,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_set_climate_mode", "climate", "set_hvac_mode", entityId,
            new Dictionary<string, object?> { ["hvac_mode"] = hvacMode }, ct);

    // ---- cover ----------------------------------------------------------

    [McpServerTool(Name = "ha_open_cover"),
     Description("Open a cover (blind, garage door, etc.). Calls cover.open_cover. Requires write mode.")]
    public static Task<string> OpenCover(HomeAssistantService svc, [Description("Cover entity id.")] string entityId, CancellationToken ct = default)
        => CallAsync(svc, "ha_open_cover", "cover", "open_cover", entityId, null, ct);

    [McpServerTool(Name = "ha_close_cover"),
     Description("Close a cover. Calls cover.close_cover. Requires write mode.")]
    public static Task<string> CloseCover(HomeAssistantService svc, [Description("Cover entity id.")] string entityId, CancellationToken ct = default)
        => CallAsync(svc, "ha_close_cover", "cover", "close_cover", entityId, null, ct);

    [McpServerTool(Name = "ha_set_cover_position"),
     Description("Set a cover's position 0 (closed) to 100 (open). Calls cover.set_cover_position. Requires write mode.")]
    public static Task<string> SetCoverPosition(
        HomeAssistantService svc,
        [Description("Cover entity id.")] string entityId,
        [Description("Target position 0-100.")] int position,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_set_cover_position", "cover", "set_cover_position", entityId,
            new Dictionary<string, object?> { ["position"] = Math.Clamp(position, 0, 100) }, ct);

    // ---- lock ----------------------------------------------------------

    [McpServerTool(Name = "ha_lock"),
     Description("Lock a lock entity. Calls lock.lock. Requires write mode.")]
    public static Task<string> Lock(HomeAssistantService svc, [Description("Lock entity id.")] string entityId, CancellationToken ct = default)
        => CallAsync(svc, "ha_lock", "lock", "lock", entityId, null, ct);

    [McpServerTool(Name = "ha_unlock"),
     Description("Unlock a lock entity. Calls lock.unlock. Requires write mode.")]
    public static Task<string> Unlock(HomeAssistantService svc, [Description("Lock entity id.")] string entityId, CancellationToken ct = default)
        => CallAsync(svc, "ha_unlock", "lock", "unlock", entityId, null, ct);

    // ---- notify --------------------------------------------------------

    [McpServerTool(Name = "ha_notify"),
     Description("Send a notification via the notify integration. Calls notify.<service>. Defaults to notify.notify. Requires write mode.")]
    public static async Task<string> Notify(
        HomeAssistantService svc,
        [Description("Notification body text.")] string message,
        [Description("Optional title.")] string? title = null,
        [Description("Optional notify service name (without the 'notify.' prefix). Defaults to 'notify'.")] string? service = null,
        [Description("Optional target identifier (e.g. mobile_app device id) — semantics depend on the notify backend.")] string? target = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableShortcuts) throw new InvalidOperationException("Shortcut tools are disabled.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed("ha_notify");
        svc.EnsureDomainAllowed("notify");

        var serviceName = string.IsNullOrWhiteSpace(service) ? "notify" : service!;
        var body = new Dictionary<string, object?> { ["message"] = message };
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title;
        if (!string.IsNullOrWhiteSpace(target)) body["target"] = target;

        var path = $"api/services/notify/{Uri.EscapeDataString(serviceName)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }

    // ---- internal helper ------------------------------------------------

    private static async Task<string> CallAsync(
        HomeAssistantService svc,
        string toolName,
        string domain,
        string service,
        string entityId,
        Dictionary<string, object?>? body,
        CancellationToken ct)
    {
        if (!svc.Options.EnableShortcuts) throw new InvalidOperationException("Shortcut tools are disabled.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed(toolName);
        svc.EnsureEntityAllowed(entityId);

        body ??= new Dictionary<string, object?>();
        body["entity_id"] = entityId;
        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }
}
