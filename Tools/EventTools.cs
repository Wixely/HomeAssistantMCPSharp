using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/events and POST /api/events/&lt;event_type&gt; — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class EventTools
{
    [McpServerTool(Name = "ha_list_event_types"),
     Description("List the events Home Assistant is currently broadcasting (event_type + listener_count). Calls GET /api/events.")]
    public static async Task<string> ListEventTypes(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableEvents) throw new InvalidOperationException("Event tools are disabled.");
        var json = await svc.GetJsonAsync("api/events", ct);
        return JsonOpts.Serialize(json);
    }

    [McpServerTool(Name = "ha_fire_event"),
     Description("Fire an event on the HA event bus. Calls POST /api/events/<event_type>. Requires write mode.")]
    public static async Task<string> FireEvent(
        HomeAssistantService svc,
        [Description("Event type to fire, e.g. 'my_custom_event'.")] string eventType,
        [Description("Optional event-data JSON object.")] string? dataJson = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableEvents) throw new InvalidOperationException("Event tools are disabled.");
        svc.EnsureWriteAllowed("ha_fire_event");

        object? body = null;
        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            using var doc = JsonDocument.Parse(dataJson);
            body = doc.RootElement.Clone();
        }
        var path = $"api/events/{Uri.EscapeDataString(eventType)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }
}
