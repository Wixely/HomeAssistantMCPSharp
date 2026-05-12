using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps POST /api/template — documented HA REST API. Returns plain text.
/// </summary>
[McpServerToolType]
public static class TemplateTools
{
    [McpServerTool(Name = "ha_render_template"),
     Description("Render a Jinja template against Home Assistant state. Calls POST /api/template. Templates are sandboxed by HA but can read any entity state — treat as a read operation.")]
    public static async Task<string> RenderTemplate(
        HomeAssistantService svc,
        [Description("Jinja template string, e.g. \"{{ states('sun.sun') }}\".")] string template,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableTemplate) throw new InvalidOperationException("Template tools are disabled.");
        var result = await svc.PostJsonAsync("api/template", new { template }, ct);
        // /api/template returns text/plain; SendAsync wraps non-JSON as {"text":"..."}.
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("text", out var text))
            return JsonSerializer.Serialize(new { rendered = text.GetString() });
        return JsonOpts.Serialize(result);
    }
}
