using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps the persistent_notification integration — useful for surfacing
/// agent feedback inside the Home Assistant UI.
/// </summary>
[McpServerToolType]
public static class NotificationTools
{
    [McpServerTool(Name = "ha_list_persistent_notifications"),
     Description("List active persistent_notification entries (notifications shown in the HA UI). Derived from GET /api/states.")]
    public static async Task<string> ListPersistentNotifications(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableNotifications) throw new InvalidOperationException("Notification tools are disabled.");
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");

        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var id)) continue;
            var entityId = id.GetString();
            if (entityId is null || !entityId.StartsWith("persistent_notification.", StringComparison.OrdinalIgnoreCase)) continue;

            string? title = null, message = null, createdAt = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                if (attrs.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (attrs.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String) createdAt = c.GetString();
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            rows.Add(new { entity_id = entityId, state, title, message, created_at = createdAt });
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_create_notification"),
     Description("Create a persistent notification shown in the Home Assistant UI. Calls persistent_notification.create. Requires write mode.")]
    public static async Task<string> CreateNotification(
        HomeAssistantService svc,
        [Description("Notification body (Markdown supported).")] string message,
        [Description("Optional notification title.")] string? title = null,
        [Description("Optional stable notification_id — pass the same id to update an existing notification.")] string? notificationId = null,
        CancellationToken ct = default)
    {
        EnsureWritable(svc, "ha_create_notification");

        var body = new Dictionary<string, object?> { ["message"] = message };
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title;
        if (!string.IsNullOrWhiteSpace(notificationId)) body["notification_id"] = notificationId;

        var result = await svc.PostJsonAsync("api/services/persistent_notification/create", body, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_dismiss_notification"),
     Description("Dismiss a persistent notification by its notification_id. Calls persistent_notification.dismiss. Requires write mode.")]
    public static async Task<string> DismissNotification(
        HomeAssistantService svc,
        [Description("The notification_id to dismiss (without the 'persistent_notification.' prefix).")] string notificationId,
        CancellationToken ct = default)
    {
        EnsureWritable(svc, "ha_dismiss_notification");
        var body = new Dictionary<string, object?> { ["notification_id"] = notificationId };
        var result = await svc.PostJsonAsync("api/services/persistent_notification/dismiss", body, ct);
        return JsonOpts.Serialize(result);
    }

    private static void EnsureWritable(HomeAssistantService svc, string toolName)
    {
        if (!svc.Options.EnableNotifications) throw new InvalidOperationException("Notification tools are disabled.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed(toolName);
        svc.EnsureDomainAllowed("persistent_notification");
    }
}
