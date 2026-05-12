using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/logbook — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class LogbookTools
{
    [McpServerTool(Name = "ha_logbook"),
     Description("Get human-readable logbook entries. Calls GET /api/logbook/<timestamp>?entity=<id>&end_time=<ts>")]
    public static async Task<string> Logbook(
        HomeAssistantService svc,
        [Description("Optional entity_id to filter on.")] string? entityId = null,
        [Description("Optional ISO-8601 start timestamp. Defaults to (now - DefaultLogbookHours).")] string? startIso = null,
        [Description("Optional ISO-8601 end timestamp.")] string? endIso = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableLogbook) throw new InvalidOperationException("Logbook tools are disabled.");
        if (!string.IsNullOrWhiteSpace(entityId)) svc.EnsureEntityAllowed(entityId);

        var start = string.IsNullOrWhiteSpace(startIso)
            ? DateTimeOffset.UtcNow.AddHours(-Math.Max(1, svc.Options.DefaultLogbookHours)).ToString("o")
            : startIso;

        var path = $"api/logbook/{Uri.EscapeDataString(start)}";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(entityId)) query.Add($"entity={Uri.EscapeDataString(entityId)}");
        if (!string.IsNullOrWhiteSpace(endIso)) query.Add($"end_time={Uri.EscapeDataString(endIso)}");
        if (query.Count > 0) path += "?" + string.Join('&', query);

        var json = await svc.GetJsonAsync(path, ct);
        return JsonOpts.Serialize(json);
    }
}
