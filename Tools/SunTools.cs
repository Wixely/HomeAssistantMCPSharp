using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// One-shot answer to "is the sun up?" via the documented sun.sun entity.
/// </summary>
[McpServerToolType]
public static class SunTools
{
    [McpServerTool(Name = "ha_get_sun"),
     Description("Get the sun.sun entity state (above_horizon / below_horizon) plus elevation, azimuth, and the next dawn/dusk/rising/setting timestamps. Derived from GET /api/states/sun.sun.")]
    public static async Task<string> GetSun(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states/sun.sun", ct);
        if (json.ValueKind != JsonValueKind.Object) return JsonOpts.Serialize(json);

        var state = json.TryGetProperty("state", out var s) ? s.GetString() : null;
        double? elevation = null, azimuth = null;
        string? nextDawn = null, nextDusk = null, nextRising = null, nextSetting = null, nextNoon = null, nextMidnight = null;
        if (json.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("elevation", out var e) && e.ValueKind == JsonValueKind.Number) elevation = e.GetDouble();
            if (attrs.TryGetProperty("azimuth", out var a) && a.ValueKind == JsonValueKind.Number) azimuth = a.GetDouble();
            if (attrs.TryGetProperty("next_dawn", out var d) && d.ValueKind == JsonValueKind.String) nextDawn = d.GetString();
            if (attrs.TryGetProperty("next_dusk", out var du) && du.ValueKind == JsonValueKind.String) nextDusk = du.GetString();
            if (attrs.TryGetProperty("next_rising", out var r) && r.ValueKind == JsonValueKind.String) nextRising = r.GetString();
            if (attrs.TryGetProperty("next_setting", out var se) && se.ValueKind == JsonValueKind.String) nextSetting = se.GetString();
            if (attrs.TryGetProperty("next_noon", out var n) && n.ValueKind == JsonValueKind.String) nextNoon = n.GetString();
            if (attrs.TryGetProperty("next_midnight", out var m) && m.ValueKind == JsonValueKind.String) nextMidnight = m.GetString();
        }
        return JsonOpts.Serialize(new
        {
            state,
            is_up = string.Equals(state, "above_horizon", StringComparison.OrdinalIgnoreCase),
            elevation,
            azimuth,
            next_dawn = nextDawn,
            next_rising = nextRising,
            next_noon = nextNoon,
            next_setting = nextSetting,
            next_dusk = nextDusk,
            next_midnight = nextMidnight,
        });
    }
}
