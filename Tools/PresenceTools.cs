using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Presence: people, zones, "who's home". All derived from GET /api/states.
/// </summary>
[McpServerToolType]
public static class PresenceTools
{
    [McpServerTool(Name = "ha_list_people"),
     Description("List every person.* entity with state (home / away / a named zone), source device, and last-changed timestamp.")]
    public static async Task<string> ListPeople(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null || !entityId.StartsWith("person.", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null, source = null, gpsAccuracy = null;
            double? latitude = null, longitude = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
                if (attrs.TryGetProperty("source", out var sr) && sr.ValueKind == JsonValueKind.String) source = sr.GetString();
                if (attrs.TryGetProperty("gps_accuracy", out var ga)) gpsAccuracy = ga.ToString();
                if (attrs.TryGetProperty("latitude", out var lat) && lat.ValueKind == JsonValueKind.Number) latitude = lat.GetDouble();
                if (attrs.TryGetProperty("longitude", out var lon) && lon.ValueKind == JsonValueKind.Number) longitude = lon.GetDouble();
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            var lastChanged = el.TryGetProperty("last_changed", out var lc) ? lc.GetString() : null;
            rows.Add(new
            {
                entity_id = entityId,
                name = friendly,
                state,
                source,
                latitude,
                longitude,
                gps_accuracy = gpsAccuracy,
                last_changed = lastChanged,
            });
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_list_zones"),
     Description("List every zone.* entity with lat/lon/radius and which persons are currently inside.")]
    public static async Task<string> ListZones(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var zones = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null || !entityId.StartsWith("zone.", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null;
            double? latitude = null, longitude = null, radius = null;
            bool? passive = null;
            string[] personsInside = Array.Empty<string>();
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
                if (attrs.TryGetProperty("latitude", out var lat) && lat.ValueKind == JsonValueKind.Number) latitude = lat.GetDouble();
                if (attrs.TryGetProperty("longitude", out var lon) && lon.ValueKind == JsonValueKind.Number) longitude = lon.GetDouble();
                if (attrs.TryGetProperty("radius", out var rd) && rd.ValueKind == JsonValueKind.Number) radius = rd.GetDouble();
                if (attrs.TryGetProperty("passive", out var pa) && (pa.ValueKind == JsonValueKind.True || pa.ValueKind == JsonValueKind.False)) passive = pa.GetBoolean();
                if (attrs.TryGetProperty("persons", out var p) && p.ValueKind == JsonValueKind.Array)
                {
                    personsInside = p.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .ToArray();
                }
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            zones.Add(new
            {
                entity_id = entityId,
                name = friendly,
                state,
                latitude,
                longitude,
                radius_m = radius,
                passive,
                persons_inside = personsInside,
                person_count = personsInside.Length,
            });
        }
        return JsonOpts.Serialize(zones);
    }

    [McpServerTool(Name = "ha_who_is_home"),
     Description("Quick answer to 'who is home?'. Returns the names of every person.* entity currently in state 'home', plus a count.")]
    public static async Task<string> WhoIsHome(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var home = new List<object>();
        var away = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null || !entityId.StartsWith("person.", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                && attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                friendly = fn.GetString();
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            var entry = new { entity_id = entityId, name = friendly, state };
            if (string.Equals(state, "home", StringComparison.OrdinalIgnoreCase)) home.Add(entry);
            else away.Add(entry);
        }
        return JsonOpts.Serialize(new
        {
            home_count = home.Count,
            home,
            away,
        });
    }
}
