using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/history/period — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class HistoryTools
{
    [McpServerTool(Name = "ha_history"),
     Description("Get state history for one or more entities. Calls GET /api/history/period/<start>?filter_entity_id=...&end_time=...")]
    public static async Task<string> History(
        HomeAssistantService svc,
        [Description("Comma-separated entity_ids to fetch history for.")] string entityIds,
        [Description("Optional ISO-8601 start timestamp. Defaults to (now - DefaultHistoryHours).")] string? startIso = null,
        [Description("Optional ISO-8601 end timestamp. Defaults to now.")] string? endIso = null,
        [Description("If true, return only the most recent state-change before <end>.")] bool minimalResponse = false,
        [Description("If true, only the significant_changes_only flag is applied (HA-side downsampling).")] bool significantChangesOnly = true,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableHistory) throw new InvalidOperationException("History tools are disabled.");
        var ids = entityIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids) svc.EnsureEntityAllowed(id);

        var start = string.IsNullOrWhiteSpace(startIso)
            ? DateTimeOffset.UtcNow.AddHours(-Math.Max(1, svc.Options.DefaultHistoryHours)).ToString("o")
            : startIso;
        var end = string.IsNullOrWhiteSpace(endIso) ? DateTimeOffset.UtcNow.ToString("o") : endIso;

        var path = $"api/history/period/{Uri.EscapeDataString(start)}"
                   + $"?filter_entity_id={Uri.EscapeDataString(string.Join(',', ids))}"
                   + $"&end_time={Uri.EscapeDataString(end)}";
        if (minimalResponse) path += "&minimal_response";
        if (significantChangesOnly) path += "&significant_changes_only";

        var json = await svc.GetJsonAsync(path, ct);

        // HA returns an array of arrays (one per entity); cap entries per entity to keep payloads sane.
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);
        var max = Math.Max(1, svc.Options.MaxHistoryEntries);
        var capped = json.EnumerateArray()
            .Select(series => series.ValueKind == JsonValueKind.Array
                ? (object)series.EnumerateArray().Take(max).ToList()
                : series)
            .ToList();
        return JsonOpts.Serialize(capped);
    }
}
