using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Registry queries (areas, floors, labels, entity-area mapping) backed by
/// Home Assistant's Jinja template helpers via POST /api/template.
/// HA's REST API doesn't expose the registry directly — template rendering
/// is the only documented, non-WebSocket way to get this information.
/// </summary>
[McpServerToolType]
public static class RegistryTools
{
    [McpServerTool(Name = "ha_list_areas"),
     Description("List every area defined in Home Assistant with its name and floor. Backed by POST /api/template using areas()/area_name()/floor_id().")]
    public static Task<string> ListAreas(HomeAssistantService svc, CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        const string template =
            "{% set out = namespace(items=[]) %}" +
            "{% for a in areas() %}" +
            "{%- set out.items = out.items + [{'area_id': a, 'name': area_name(a), 'floor_id': floor_id(a)}] -%}" +
            "{% endfor %}" +
            "{{ out.items | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    [McpServerTool(Name = "ha_list_floors"),
     Description("List every floor defined in Home Assistant with its name. Backed by POST /api/template using floors()/floor_name().")]
    public static Task<string> ListFloors(HomeAssistantService svc, CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        const string template =
            "{% set out = namespace(items=[]) %}" +
            "{% for f in floors() %}" +
            "{%- set out.items = out.items + [{'floor_id': f, 'name': floor_name(f)}] -%}" +
            "{% endfor %}" +
            "{{ out.items | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    [McpServerTool(Name = "ha_list_labels"),
     Description("List every label defined in Home Assistant. Backed by POST /api/template using labels()/label_name().")]
    public static Task<string> ListLabels(HomeAssistantService svc, CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        const string template =
            "{% set out = namespace(items=[]) %}" +
            "{% for l in labels() %}" +
            "{%- set out.items = out.items + [{'label_id': l, 'name': label_name(l)}] -%}" +
            "{% endfor %}" +
            "{{ out.items | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    [McpServerTool(Name = "ha_entities_in_area"),
     Description("List all entity_ids assigned to an area. Accepts the area name or area_id. Backed by area_entities().")]
    public static Task<string> EntitiesInArea(
        HomeAssistantService svc,
        [Description("Area name or area_id, e.g. 'Kitchen' or 'kitchen'.")] string area,
        CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        if (string.IsNullOrWhiteSpace(area)) throw new ArgumentException("area is required.", nameof(area));
        var template = "{{ area_entities(" + JinjaString(area) + ") | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    [McpServerTool(Name = "ha_devices_in_area"),
     Description("List all device_ids assigned to an area. Accepts the area name or area_id. Backed by area_devices().")]
    public static Task<string> DevicesInArea(
        HomeAssistantService svc,
        [Description("Area name or area_id.")] string area,
        CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        if (string.IsNullOrWhiteSpace(area)) throw new ArgumentException("area is required.", nameof(area));
        var template = "{{ area_devices(" + JinjaString(area) + ") | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    [McpServerTool(Name = "ha_areas_on_floor"),
     Description("List the area_ids on a given floor. Accepts the floor name or floor_id. Backed by floor_areas().")]
    public static Task<string> AreasOnFloor(
        HomeAssistantService svc,
        [Description("Floor name or floor_id.")] string floor,
        CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        if (string.IsNullOrWhiteSpace(floor)) throw new ArgumentException("floor is required.", nameof(floor));
        var template = "{{ floor_areas(" + JinjaString(floor) + ") | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    [McpServerTool(Name = "ha_entity_area"),
     Description("Get the area name and area_id for an entity (or device). Reverse of ha_entities_in_area.")]
    public static Task<string> EntityArea(
        HomeAssistantService svc,
        [Description("Entity id or device id.")] string lookup,
        CancellationToken ct = default)
    {
        EnsureEnabled(svc);
        if (string.IsNullOrWhiteSpace(lookup)) throw new ArgumentException("lookup is required.", nameof(lookup));
        var arg = JinjaString(lookup);
        var template = "{{ {'area_id': area_id(" + arg + "), 'area_name': area_name(" + arg + ")} | tojson }}";
        return RenderTemplateAsRawJsonAsync(svc, template, ct);
    }

    private static void EnsureEnabled(HomeAssistantService svc)
    {
        if (!svc.Options.EnableRegistry) throw new InvalidOperationException("Registry tools are disabled.");
        if (!svc.Options.EnableTemplate) throw new InvalidOperationException("Template rendering is disabled — registry tools require it.");
    }

    /// <summary>
    /// Renders a template that produces JSON text and returns it verbatim.
    /// HomeAssistantService wraps non-JSON template output as { "text": "..." }; we unwrap it.
    /// </summary>
    private static async Task<string> RenderTemplateAsRawJsonAsync(HomeAssistantService svc, string template, CancellationToken ct)
    {
        var result = await svc.PostJsonAsync("api/template", new { template }, ct);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            var raw = text.GetString() ?? "";
            try { using var doc = JsonDocument.Parse(raw); return JsonOpts.Serialize(doc.RootElement); }
            catch (JsonException) { return JsonOpts.Serialize(new { rendered = raw }); }
        }
        return JsonOpts.Serialize(result);
    }

    /// <summary>Encode a value as a Jinja string literal with single quotes (escape embedded quotes and backslashes).</summary>
    private static string JinjaString(string value)
        => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
