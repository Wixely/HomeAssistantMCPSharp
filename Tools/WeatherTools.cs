using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Weather entities and forecast lookups.
/// Listing is derived from /api/states. Forecast uses POST /api/services/weather/get_forecasts
/// with return_response=true (Home Assistant ≥ 2024.8 for REST service-response support).
/// </summary>
[McpServerToolType]
public static class WeatherTools
{
    [McpServerTool(Name = "ha_list_weather"),
     Description("List every weather.* entity with current condition, temperature, humidity, pressure, wind, and unit information.")]
    public static async Task<string> ListWeather(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null || !entityId.StartsWith("weather.", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null, tempUnit = null, pressureUnit = null, windUnit = null, visibilityUnit = null, precipitationUnit = null;
            double? temperature = null, humidity = null, pressure = null, windSpeed = null, windBearing = null, visibility = null, dewPoint = null;
            string? windBearingText = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
                if (attrs.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number) temperature = t.GetDouble();
                if (attrs.TryGetProperty("temperature_unit", out var tu) && tu.ValueKind == JsonValueKind.String) tempUnit = tu.GetString();
                if (attrs.TryGetProperty("humidity", out var h) && h.ValueKind == JsonValueKind.Number) humidity = h.GetDouble();
                if (attrs.TryGetProperty("pressure", out var p) && p.ValueKind == JsonValueKind.Number) pressure = p.GetDouble();
                if (attrs.TryGetProperty("pressure_unit", out var pu) && pu.ValueKind == JsonValueKind.String) pressureUnit = pu.GetString();
                if (attrs.TryGetProperty("wind_speed", out var w) && w.ValueKind == JsonValueKind.Number) windSpeed = w.GetDouble();
                if (attrs.TryGetProperty("wind_speed_unit", out var wu) && wu.ValueKind == JsonValueKind.String) windUnit = wu.GetString();
                if (attrs.TryGetProperty("wind_bearing", out var wb))
                {
                    if (wb.ValueKind == JsonValueKind.Number) windBearing = wb.GetDouble();
                    else if (wb.ValueKind == JsonValueKind.String) windBearingText = wb.GetString();
                }
                if (attrs.TryGetProperty("visibility", out var vis) && vis.ValueKind == JsonValueKind.Number) visibility = vis.GetDouble();
                if (attrs.TryGetProperty("visibility_unit", out var visU) && visU.ValueKind == JsonValueKind.String) visibilityUnit = visU.GetString();
                if (attrs.TryGetProperty("precipitation_unit", out var prU) && prU.ValueKind == JsonValueKind.String) precipitationUnit = prU.GetString();
                if (attrs.TryGetProperty("dew_point", out var dp) && dp.ValueKind == JsonValueKind.Number) dewPoint = dp.GetDouble();
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            rows.Add(new
            {
                entity_id = entityId,
                friendly_name = friendly,
                condition = state,
                temperature,
                temperature_unit = tempUnit,
                humidity,
                pressure,
                pressure_unit = pressureUnit,
                wind_speed = windSpeed,
                wind_speed_unit = windUnit,
                wind_bearing = windBearing,
                wind_bearing_text = windBearingText,
                visibility,
                visibility_unit = visibilityUnit,
                precipitation_unit = precipitationUnit,
                dew_point = dewPoint,
            });
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_get_forecast"),
     Description("Get a forecast for a weather entity. Calls weather.get_forecasts (HA ≥ 2024.6) with return_response=true (REST service-response support requires HA ≥ 2024.8). Read-only — no state changes. Pass forecastType='daily', 'hourly', or 'twice_daily'.")]
    public static async Task<string> GetForecast(
        HomeAssistantService svc,
        [Description("Weather entity id, e.g. 'weather.home'.")] string entityId,
        [Description("Forecast type: 'daily', 'hourly', or 'twice_daily'. Defaults to 'daily'.")] string? forecastType = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureEntityAllowed(entityId);

        var type = string.IsNullOrWhiteSpace(forecastType) ? "daily" : forecastType!.Trim().ToLowerInvariant();
        if (type is not ("daily" or "hourly" or "twice_daily"))
            throw new ArgumentException("forecastType must be 'daily', 'hourly', or 'twice_daily'.", nameof(forecastType));

        var body = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["type"] = type,
        };
        // return_response is a query flag on the REST service endpoint (HA ≥ 2024.8).
        var result = await svc.PostJsonAsync("api/services/weather/get_forecasts?return_response", body, ct);
        return JsonOpts.Serialize(result);
    }
}
