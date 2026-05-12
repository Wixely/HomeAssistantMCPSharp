using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Camera-related service helpers. Disabled by default because camera.snapshot
/// writes a file on the Home Assistant host's filesystem.
/// </summary>
[McpServerToolType]
public static class CameraTools
{
    [McpServerTool(Name = "ha_camera_snapshot"),
     Description("Capture a snapshot from a camera entity and write it to a file on the Home Assistant host. " +
                 "Calls camera.snapshot. Disabled by default — set HomeAssistant:EnableCameraSnapshot=true to enable. " +
                 "The filename path is resolved on the HA host and must be in an allow-listed directory there. Requires write mode.")]
    public static async Task<string> Snapshot(
        HomeAssistantService svc,
        [Description("Camera entity id, e.g. 'camera.front_door'.")] string entityId,
        [Description("Destination filename on the Home Assistant host, e.g. '/config/www/snapshot.jpg'.")] string filename,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableCameraSnapshot) throw new InvalidOperationException("Camera snapshot is disabled. Set HomeAssistant:EnableCameraSnapshot=true to enable.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed("ha_camera_snapshot");
        svc.EnsureEntityAllowed(entityId);
        if (string.IsNullOrWhiteSpace(filename)) throw new ArgumentException("filename is required.", nameof(filename));

        var body = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["filename"] = filename,
        };
        var result = await svc.PostJsonAsync("api/services/camera/snapshot", body, ct);
        return JsonOpts.Serialize(result);
    }
}
