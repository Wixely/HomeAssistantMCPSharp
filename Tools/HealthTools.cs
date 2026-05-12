using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Device-health diagnostics derived from /api/states:
/// batteries that need attention and entities reporting unavailable/unknown.
/// </summary>
[McpServerToolType]
public static class HealthTools
{
    [McpServerTool(Name = "ha_list_batteries"),
     Description("List every entity reporting a battery level (device_class=battery) or battery_charging status. Sorted lowest-first; entities at or below HomeAssistant:LowBatteryThresholdPct are flagged 'low'.")]
    public static async Task<string> ListBatteries(
        HomeAssistantService svc,
        [Description("Optional override for the low-battery threshold (percent). Defaults to HomeAssistant:LowBatteryThresholdPct.")] int? lowThresholdPct = null,
        [Description("If true, return only entities at or below the low threshold.")] bool onlyLow = false,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var threshold = Math.Clamp(lowThresholdPct ?? svc.Options.LowBatteryThresholdPct, 0, 100);
        var rows = new List<(string entity_id, string? friendly_name, double? percent, string? state, string? deviceClass, bool low)>();

        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null) continue;

            string? friendly = null, deviceClass = null, unit = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
                if (attrs.TryGetProperty("device_class", out var dc) && dc.ValueKind == JsonValueKind.String) deviceClass = dc.GetString();
                if (attrs.TryGetProperty("unit_of_measurement", out var u) && u.ValueKind == JsonValueKind.String) unit = u.GetString();
            }

            // Accept device_class=battery (% sensor) or device_class=battery_charging (binary_sensor),
            // plus the common naming fallback for legacy integrations that omit device_class.
            bool isBatteryPct = string.Equals(deviceClass, "battery", StringComparison.OrdinalIgnoreCase)
                                || (entityId.StartsWith("sensor.", StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(unit, "%", StringComparison.Ordinal)
                                    && (entityId.Contains("_battery", StringComparison.OrdinalIgnoreCase)
                                        || entityId.Contains("battery_level", StringComparison.OrdinalIgnoreCase)));
            bool isCharging = string.Equals(deviceClass, "battery_charging", StringComparison.OrdinalIgnoreCase);
            if (!isBatteryPct && !isCharging) continue;

            var rawState = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            double? pct = null;
            if (isBatteryPct && double.TryParse(rawState, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                pct = parsed;
            bool low = pct.HasValue && pct.Value <= threshold;
            if (onlyLow && !low) continue;

            rows.Add((entityId, friendly, pct, rawState, isCharging ? "battery_charging" : "battery", low));
        }

        var ordered = rows
            .OrderBy(r => r.percent ?? double.MaxValue)
            .ThenBy(r => r.entity_id)
            .Select(r => new
            {
                entity_id = r.entity_id,
                friendly_name = r.friendly_name,
                percent = r.percent,
                state = r.state,
                device_class = r.deviceClass,
                low = r.low,
            });
        return JsonOpts.Serialize(new
        {
            threshold_pct = threshold,
            low_count = rows.Count(r => r.low),
            total_count = rows.Count,
            entities = ordered,
        });
    }

    [McpServerTool(Name = "ha_list_unavailable"),
     Description("List every entity currently reporting state 'unavailable' or 'unknown', grouped by domain. Useful for diagnosing integration outages.")]
    public static async Task<string> ListUnavailable(
        HomeAssistantService svc,
        [Description("Optional state filter: 'unavailable' or 'unknown'. Default returns both.")] string? stateFilter = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var groups = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (string.IsNullOrEmpty(entityId)) continue;
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;

            bool match = stateFilter is null
                ? (string.Equals(state, "unavailable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase))
                : string.Equals(state, stateFilter, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            var dot = entityId.IndexOf('.');
            var domain = dot > 0 ? entityId[..dot] : entityId;
            string? friendly = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                && attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                friendly = fn.GetString();
            }
            var lastChanged = el.TryGetProperty("last_changed", out var lc) ? lc.GetString() : null;

            if (!groups.TryGetValue(domain, out var list))
            {
                list = new List<object>();
                groups[domain] = list;
            }
            list.Add(new { entity_id = entityId, friendly_name = friendly, state, last_changed = lastChanged });
            total++;
        }

        return JsonOpts.Serialize(new
        {
            total,
            by_domain = groups
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => new { domain = kv.Key, count = kv.Value.Count, entities = kv.Value }),
        });
    }
}
