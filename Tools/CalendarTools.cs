using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/calendars and GET /api/calendars/&lt;entity_id&gt; — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class CalendarTools
{
    [McpServerTool(Name = "ha_list_calendars"),
     Description("List calendar entities. Calls GET /api/calendars.")]
    public static async Task<string> ListCalendars(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableCalendar) throw new InvalidOperationException("Calendar tools are disabled.");
        var result = await svc.GetJsonAsync("api/calendars", ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_calendar_events"),
     Description("Get events from a calendar entity between two timestamps. Calls GET /api/calendars/<entity_id>?start=<iso>&end=<iso>.")]
    public static async Task<string> CalendarEvents(
        HomeAssistantService svc,
        [Description("Calendar entity_id, e.g. 'calendar.work'.")] string entityId,
        [Description("ISO-8601 start timestamp.")] string startIso,
        [Description("ISO-8601 end timestamp.")] string endIso,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableCalendar) throw new InvalidOperationException("Calendar tools are disabled.");
        svc.EnsureEntityAllowed(entityId);
        var path = $"api/calendars/{Uri.EscapeDataString(entityId)}"
                   + $"?start={Uri.EscapeDataString(startIso)}"
                   + $"&end={Uri.EscapeDataString(endIso)}";
        var result = await svc.GetJsonAsync(path, ct);
        return JsonOpts.Serialize(result);
    }
}
