using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Electricity / energy sensor helpers built on top of the documented REST API.
///
/// Home Assistant's Energy dashboard preferences and long-term statistics
/// (statistics_during_period, energy/get_prefs, fossil_energy_consumption)
/// are WebSocket-only and not reachable from REST. These tools instead
/// classify and aggregate the energy-related sensors already exposed in
/// /api/states, and fetch their /api/history series.
/// </summary>
[McpServerToolType]
public static class EnergyTools
{
    // Recognised device_class values for electricity-related sensors.
    // See https://www.home-assistant.io/integrations/sensor/#device-class
    private static readonly HashSet<string> ElectricityDeviceClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "power", "energy", "energy_storage", "current", "voltage",
        "apparent_power", "reactive_power", "power_factor", "frequency",
        "monetary",
    };

    // Units we treat as energy/power even if device_class is missing (common
    // with template sensors or older integrations).
    private static readonly HashSet<string> EnergyUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "W", "kW", "MW", "Wh", "kWh", "MWh", "VA", "kVA", "var", "kvar",
        "A", "mA", "V", "mV", "Hz",
    };

    [McpServerTool(Name = "ha_list_energy_sensors"),
     Description("List every electricity-related sensor in Home Assistant — power, energy, voltage, current, frequency, and electricity cost. Derived from GET /api/states by device_class and unit_of_measurement.")]
    public static async Task<string> ListEnergySensors(
        HomeAssistantService svc,
        [Description("Optional device_class filter, e.g. 'power', 'energy', 'monetary'.")] string? deviceClass = null,
        [Description("Optional substring filter against entity_id or friendly_name.")] string? contains = null,
        CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!TryClassify(el, out var info)) continue;
            if (deviceClass is not null && !string.Equals(info.DeviceClass, deviceClass, StringComparison.OrdinalIgnoreCase)) continue;
            if (contains is not null
                && !info.EntityId.Contains(contains, StringComparison.OrdinalIgnoreCase)
                && !(info.FriendlyName?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }
            rows.Add(new
            {
                entity_id = info.EntityId,
                friendly_name = info.FriendlyName,
                state = info.RawState,
                value = info.Value,
                unit = info.Unit,
                device_class = info.DeviceClass,
                state_class = info.StateClass,
                last_changed = info.LastChanged,
                category = info.Category,
            });
        }
        return JsonOpts.Serialize(rows.OrderBy(r => ((dynamic)r).category).ThenBy(r => ((dynamic)r).entity_id));
    }

    [McpServerTool(Name = "ha_get_energy_summary"),
     Description("One-shot electricity overview: current total power draw, per-sensor power readings, today's energy totals, and active electricity cost trackers. " +
                 "Caveat: 'total_power_w' simply sums every sensor with device_class=power — it WILL double-count if you have both a whole-house meter and per-circuit meters. Use 'power_readings' for an unambiguous breakdown.")]
    public static async Task<string> GetEnergySummary(HomeAssistantService svc, CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var power = new List<object>();
        var energyTotal = new List<object>();
        var voltage = new List<object>();
        var current = new List<object>();
        var cost = new List<object>();
        double totalPowerW = 0;
        bool sawPower = false;

        foreach (var el in json.EnumerateArray())
        {
            if (!TryClassify(el, out var info)) continue;
            if (!info.Value.HasValue) continue;

            switch (info.DeviceClass?.ToLowerInvariant())
            {
                case "power":
                    sawPower = true;
                    var watts = NormaliseToWatts(info.Value.Value, info.Unit);
                    totalPowerW += watts;
                    power.Add(new { entity_id = info.EntityId, friendly_name = info.FriendlyName, value = info.Value, unit = info.Unit, watts });
                    break;
                case "energy":
                    // Cumulative totals — surface state_class=total_increasing for today's consumption.
                    if (string.Equals(info.StateClass, "total_increasing", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(info.StateClass, "total", StringComparison.OrdinalIgnoreCase))
                    {
                        energyTotal.Add(new { entity_id = info.EntityId, friendly_name = info.FriendlyName, value = info.Value, unit = info.Unit, last_changed = info.LastChanged });
                    }
                    break;
                case "voltage":
                    voltage.Add(new { entity_id = info.EntityId, friendly_name = info.FriendlyName, value = info.Value, unit = info.Unit });
                    break;
                case "current":
                    current.Add(new { entity_id = info.EntityId, friendly_name = info.FriendlyName, value = info.Value, unit = info.Unit });
                    break;
                case "monetary":
                    cost.Add(new { entity_id = info.EntityId, friendly_name = info.FriendlyName, value = info.Value, unit = info.Unit });
                    break;
            }
        }

        return JsonOpts.Serialize(new
        {
            total_power_w = sawPower ? Math.Round(totalPowerW, 3) : (double?)null,
            total_power_kw = sawPower ? Math.Round(totalPowerW / 1000.0, 4) : (double?)null,
            power_readings = power,
            energy_totals = energyTotal,
            voltage_readings = voltage,
            current_readings = current,
            cost_trackers = cost,
            caveat = "total_power_w naively sums all power sensors and may double-count when both whole-house and per-circuit meters exist.",
        });
    }

    [McpServerTool(Name = "ha_get_energy_history"),
     Description("Fetch the recent history of a single energy/power sensor and return computed min/max/avg/last alongside the raw samples. Calls GET /api/history/period/<start>.")]
    public static async Task<string> GetEnergyHistory(
        HomeAssistantService svc,
        [Description("Sensor entity id, e.g. 'sensor.house_power' or 'sensor.electricity_today'.")] string entityId,
        [Description("How many hours back to look. Defaults to HomeAssistant:DefaultHistoryHours.")] int? hours = null,
        CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        if (!svc.Options.EnableHistory) throw new InvalidOperationException("History tools are disabled.");
        svc.EnsureEntityAllowed(entityId);

        var window = Math.Max(1, hours ?? svc.Options.DefaultHistoryHours);
        var start = DateTimeOffset.UtcNow.AddHours(-window).ToString("o");
        var end = DateTimeOffset.UtcNow.ToString("o");
        var path = $"api/history/period/{Uri.EscapeDataString(start)}"
                   + $"?filter_entity_id={Uri.EscapeDataString(entityId)}"
                   + $"&end_time={Uri.EscapeDataString(end)}"
                   + "&significant_changes_only";

        var json = await svc.GetJsonAsync(path, ct);
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() == 0)
            return JsonOpts.Serialize(new { entity_id = entityId, hours = window, samples = Array.Empty<object>() });

        var max = Math.Max(1, svc.Options.MaxHistoryEntries);
        var samples = new List<(DateTimeOffset When, double Value, string? RawState)>(max);
        string? unit = null, friendly = null, deviceClass = null, stateClass = null;

        foreach (var sample in json[0].EnumerateArray().Take(max))
        {
            if (sample.ValueKind != JsonValueKind.Object) continue;
            var rawState = sample.TryGetProperty("state", out var s) ? s.GetString() : null;
            var lastChanged = sample.TryGetProperty("last_changed", out var lc) ? lc.GetString() : null;
            if (sample.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (unit is null && attrs.TryGetProperty("unit_of_measurement", out var u) && u.ValueKind == JsonValueKind.String) unit = u.GetString();
                if (friendly is null && attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
                if (deviceClass is null && attrs.TryGetProperty("device_class", out var dc) && dc.ValueKind == JsonValueKind.String) deviceClass = dc.GetString();
                if (stateClass is null && attrs.TryGetProperty("state_class", out var sc) && sc.ValueKind == JsonValueKind.String) stateClass = sc.GetString();
            }
            if (double.TryParse(rawState, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && DateTimeOffset.TryParse(lastChanged, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                samples.Add((when, v, rawState));
            }
        }

        if (samples.Count == 0)
        {
            return JsonOpts.Serialize(new
            {
                entity_id = entityId, hours = window,
                friendly_name = friendly, unit, device_class = deviceClass, state_class = stateClass,
                samples = Array.Empty<object>(),
                note = "No numeric samples in the requested window.",
            });
        }

        var first = samples[0];
        var last = samples[^1];
        var min = samples.Min(x => x.Value);
        var max2 = samples.Max(x => x.Value);
        var avg = samples.Average(x => x.Value);
        double? delta = null;
        if (string.Equals(stateClass, "total_increasing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stateClass, "total", StringComparison.OrdinalIgnoreCase))
        {
            delta = Math.Round(last.Value - first.Value, 6);
        }

        return JsonOpts.Serialize(new
        {
            entity_id = entityId,
            friendly_name = friendly,
            unit,
            device_class = deviceClass,
            state_class = stateClass,
            hours = window,
            sample_count = samples.Count,
            first = new { when = first.When, value = first.Value },
            last = new { when = last.When, value = last.Value },
            min = Math.Round(min, 6),
            max = Math.Round(max2, 6),
            avg = Math.Round(avg, 6),
            delta_total_state = delta,
            samples = samples.Select(x => new { when = x.When, value = x.Value }),
        });
    }

    // ---- classification helpers ----------------------------------------

    private record SensorInfo(
        string EntityId,
        string? FriendlyName,
        string? RawState,
        double? Value,
        string? Unit,
        string? DeviceClass,
        string? StateClass,
        string? LastChanged,
        string Category);

    private static bool TryClassify(JsonElement el, out SensorInfo info)
    {
        info = null!;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("entity_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return false;
        var entityId = idEl.GetString()!;
        if (!entityId.StartsWith("sensor.", StringComparison.OrdinalIgnoreCase)) return false;

        string? friendly = null, unit = null, deviceClass = null, stateClass = null;
        if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
            if (attrs.TryGetProperty("unit_of_measurement", out var u) && u.ValueKind == JsonValueKind.String) unit = u.GetString();
            if (attrs.TryGetProperty("device_class", out var dc) && dc.ValueKind == JsonValueKind.String) deviceClass = dc.GetString();
            if (attrs.TryGetProperty("state_class", out var sc) && sc.ValueKind == JsonValueKind.String) stateClass = sc.GetString();
        }

        bool dcMatch = deviceClass is not null && ElectricityDeviceClasses.Contains(deviceClass);
        bool unitMatch = unit is not null && EnergyUnits.Contains(unit);
        // monetary unit could be anything (currency code) — gate on device_class only for cost.
        if (!dcMatch && !unitMatch) return false;

        var rawState = el.TryGetProperty("state", out var s) ? s.GetString() : null;
        double? value = null;
        if (double.TryParse(rawState, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            value = parsed;

        var lastChanged = el.TryGetProperty("last_changed", out var lc) ? lc.GetString() : null;
        var category = CategoriseSensor(deviceClass, unit);
        info = new SensorInfo(entityId, friendly, rawState, value, unit, deviceClass, stateClass, lastChanged, category);
        return true;
    }

    private static string CategoriseSensor(string? deviceClass, string? unit)
    {
        if (deviceClass is not null)
        {
            return deviceClass.ToLowerInvariant() switch
            {
                "power" or "apparent_power" or "reactive_power" or "power_factor" => "power",
                "energy" or "energy_storage" => "energy",
                "voltage" => "voltage",
                "current" => "current",
                "frequency" => "frequency",
                "monetary" => "cost",
                _ => "other",
            };
        }
        return unit switch
        {
            "W" or "kW" or "MW" or "VA" or "kVA" or "var" or "kvar" => "power",
            "Wh" or "kWh" or "MWh" => "energy",
            "V" or "mV" => "voltage",
            "A" or "mA" => "current",
            "Hz" => "frequency",
            _ => "other",
        };
    }

    private static double NormaliseToWatts(double value, string? unit)
        => unit?.ToLowerInvariant() switch
        {
            "kw" => value * 1000.0,
            "mw" => value * 1_000_000.0,
            _ => value, // assume W (or unknown — value already in source units)
        };

    private static void EnsureEnabled(HomeAssistantService svc)
    {
        if (!svc.Options.EnableEnergy) throw new InvalidOperationException("Energy tools are disabled.");
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
    }
}
