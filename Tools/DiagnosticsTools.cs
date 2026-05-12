using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/error_log — documented HA REST API — plus a self-info aggregator.
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    [McpServerTool(Name = "ha_error_log"),
     Description("Get the tail of the Home Assistant error log (home-assistant.log). Calls GET /api/error_log.")]
    public static async Task<string> ErrorLog(
        HomeAssistantService svc,
        [Description("Maximum lines to return from the tail (default 200).")] int maxLines = 200,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableDiagnostics) throw new InvalidOperationException("Diagnostics tools are disabled.");
        var text = await svc.GetStringAsync("api/error_log", ct);
        if (string.IsNullOrWhiteSpace(text)) return "{\"lines\":[]}";

        var allLines = text.Split('\n');
        var take = Math.Max(1, maxLines);
        var lines = allLines.Length <= take
            ? allLines
            : allLines[(allLines.Length - take)..];
        return JsonOpts.Serialize(new { count = lines.Length, lines });
    }

    [McpServerTool(Name = "ha_server_info"),
     Description("Return a summary of this MCP bridge's own configuration (read-only flag, enabled features, target HA URL, version).")]
    public static string ServerInfo(HomeAssistantService svc)
    {
        var o = svc.Options;
        var info = new
        {
            BaseAddress = svc.BaseAddress?.ToString(),
            o.ReadOnly,
            Features = new
            {
                o.EnableStates,
                o.EnableServices,
                o.EnableHistory,
                o.EnableLogbook,
                o.EnableTemplate,
                o.EnableConversation,
                o.EnableCalendar,
                o.EnableDiagnostics,
                o.EnableEvents,
            },
            o.AllowedEntities,
            o.BlockedEntities,
            o.AllowedDomains,
            o.BlockedDomains,
            o.MaxStatesReturned,
            o.MaxHistoryEntries,
            o.DefaultHistoryHours,
            o.DefaultLogbookHours,
            Version = typeof(DiagnosticsTools).Assembly.GetName().Version?.ToString(),
        };
        return JsonOpts.Serialize(info);
    }
}
