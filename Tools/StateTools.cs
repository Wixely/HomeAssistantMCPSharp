using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET/POST/DELETE /api/states[/<entity_id>] — documented Home Assistant REST API.
/// </summary>
[McpServerToolType]
public static class StateTools
{
    [McpServerTool(Name = "ha_ping"),
     Description("Check connectivity to Home Assistant. Calls GET /api/ which simply returns an API-running message.")]
    public static async Task<string> Ping(HomeAssistantService svc, CancellationToken ct)
    {
        var result = await svc.GetJsonAsync("api/", ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_list_states"),
     Description("List the state of every entity Home Assistant knows about. Optional domain filter (e.g. 'light').")]
    public static async Task<string> ListStates(
        HomeAssistantService svc,
        [Description("Optional entity domain filter, e.g. 'light', 'switch', 'sensor'.")] string? domain = null,
        [Description("Optional substring filter applied to entity_id (case-insensitive).")] string? contains = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var truncate = Math.Max(0, svc.Options.AttributeValueTruncate);
        var max = Math.Max(1, svc.Options.MaxStatesReturned);

        var filtered = json.EnumerateArray()
            .Select(el => new
            {
                EntityId = el.TryGetProperty("entity_id", out var id) ? id.GetString() : null,
                State = el.TryGetProperty("state", out var s) ? s.GetString() : null,
                LastChanged = el.TryGetProperty("last_changed", out var lc) ? lc.GetString() : null,
                LastUpdated = el.TryGetProperty("last_updated", out var lu) ? lu.GetString() : null,
                Attributes = el.TryGetProperty("attributes", out var a) ? TruncateAttributes(a, truncate) : null,
            })
            .Where(x => x.EntityId is not null)
            .Where(x => domain is null || x.EntityId!.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase))
            .Where(x => contains is null || x.EntityId!.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .ToList();

        return JsonOpts.Serialize(filtered);
    }

    [McpServerTool(Name = "ha_get_state"),
     Description("Get the current state of a single entity. Calls GET /api/states/<entity_id>.")]
    public static async Task<string> GetState(
        HomeAssistantService svc,
        [Description("Entity id, e.g. 'light.kitchen' or 'sensor.outside_temperature'.")] string entityId,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        svc.EnsureEntityAllowed(entityId);
        var result = await svc.GetJsonAsync($"api/states/{Uri.EscapeDataString(entityId)}", ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_set_state"),
     Description("Create or update an entity state in HA's state machine. Note: this does NOT actuate devices — for that, use ha_call_service. Calls POST /api/states/<entity_id>. Requires write mode.")]
    public static async Task<string> SetState(
        HomeAssistantService svc,
        [Description("Entity id, e.g. 'input_text.note'.")] string entityId,
        [Description("New state value.")] string state,
        [Description("Optional attributes JSON object, e.g. '{\"unit_of_measurement\":\"°C\"}'.")] string? attributesJson = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        svc.EnsureWriteAllowed("ha_set_state");
        svc.EnsureEntityAllowed(entityId);

        var body = new Dictionary<string, object?> { ["state"] = state };
        if (!string.IsNullOrWhiteSpace(attributesJson))
        {
            using var doc = JsonDocument.Parse(attributesJson);
            body["attributes"] = doc.RootElement.Clone();
        }
        var result = await svc.PostJsonAsync($"api/states/{Uri.EscapeDataString(entityId)}", body, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_delete_state"),
     Description("Remove an entity from HA's state machine. Note: this does NOT delete a configured device or integration — it only forgets the current state. Calls DELETE /api/states/<entity_id>. Requires write mode.")]
    public static async Task<string> DeleteState(
        HomeAssistantService svc,
        [Description("Entity id to remove from the state machine.")] string entityId,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        svc.EnsureWriteAllowed("ha_delete_state");
        svc.EnsureEntityAllowed(entityId);
        var result = await svc.DeleteJsonAsync($"api/states/{Uri.EscapeDataString(entityId)}", ct);
        return JsonOpts.Serialize(result);
    }

    private static object? TruncateAttributes(JsonElement attrs, int truncate)
    {
        if (attrs.ValueKind != JsonValueKind.Object || truncate <= 0) return attrs;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in attrs.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString() ?? string.Empty;
                dict[prop.Name] = s.Length > truncate ? s[..truncate] + "…(truncated)" : s;
            }
            else
            {
                dict[prop.Name] = prop.Value;
            }
        }
        return dict;
    }
}
