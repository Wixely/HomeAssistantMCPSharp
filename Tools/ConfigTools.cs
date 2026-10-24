using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps GET /api/config — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class ConfigTools
{
    [McpServerTool(Name = "ha_get_config"),
     Description("Get the Home Assistant configuration (version, location, unit system, components). Calls GET /api/config.")]
    public static async Task<string> GetConfig(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State/config tools are disabled.");
        var result = await svc.GetJsonAsync("api/config", ct);
        return JsonOpts.Serialize(result);
    }
}
