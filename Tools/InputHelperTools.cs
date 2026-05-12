using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Typed write helpers for the input_* helper domains. These are commonly
/// used by agents as persistent state stores between conversations.
/// All call POST /api/services/&lt;domain&gt;/set_value (or select_option).
/// </summary>
[McpServerToolType]
public static class InputHelperTools
{
    [McpServerTool(Name = "ha_set_input_number"),
     Description("Set the value of an input_number helper. Calls input_number.set_value. Requires write mode.")]
    public static Task<string> SetInputNumber(
        HomeAssistantService svc,
        [Description("input_number entity id, e.g. 'input_number.target_temp'.")] string entityId,
        [Description("Numeric value within the helper's configured min/max range.")] double value,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_set_input_number", "input_number", "set_value", entityId,
            new Dictionary<string, object?> { ["value"] = value }, ct);

    [McpServerTool(Name = "ha_set_input_text"),
     Description("Set the value of an input_text helper. Calls input_text.set_value. Requires write mode.")]
    public static Task<string> SetInputText(
        HomeAssistantService svc,
        [Description("input_text entity id, e.g. 'input_text.note'.")] string entityId,
        [Description("Text value. Must satisfy the helper's configured min/max length and pattern (if any).")] string value,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_set_input_text", "input_text", "set_value", entityId,
            new Dictionary<string, object?> { ["value"] = value }, ct);

    [McpServerTool(Name = "ha_set_input_select"),
     Description("Choose an option on an input_select helper. Calls input_select.select_option. The option must match one of the helper's configured options. Requires write mode.")]
    public static Task<string> SetInputSelect(
        HomeAssistantService svc,
        [Description("input_select entity id, e.g. 'input_select.house_mode'.")] string entityId,
        [Description("Option text (must match an existing option on the helper).")] string option,
        CancellationToken ct = default)
        => CallAsync(svc, "ha_set_input_select", "input_select", "select_option", entityId,
            new Dictionary<string, object?> { ["option"] = option }, ct);

    [McpServerTool(Name = "ha_set_input_datetime"),
     Description("Set the value of an input_datetime helper. Calls input_datetime.set_datetime. " +
                 "Pass datetime as ISO-8601 ('2026-01-15T18:30:00'), or one of date / time individually depending on the helper's mode. " +
                 "Requires write mode.")]
    public static Task<string> SetInputDatetime(
        HomeAssistantService svc,
        [Description("input_datetime entity id, e.g. 'input_datetime.bedtime'.")] string entityId,
        [Description("Full datetime, ISO-8601 'YYYY-MM-DDTHH:MM:SS'. Use for helpers configured with date+time.")] string? datetime = null,
        [Description("Date-only value 'YYYY-MM-DD'. Use for date-only helpers.")] string? date = null,
        [Description("Time-only value 'HH:MM:SS'. Use for time-only helpers.")] string? time = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(datetime) && string.IsNullOrWhiteSpace(date) && string.IsNullOrWhiteSpace(time))
            throw new ArgumentException("At least one of datetime, date, or time must be provided.");

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(datetime)) body["datetime"] = datetime;
        if (!string.IsNullOrWhiteSpace(date)) body["date"] = date;
        if (!string.IsNullOrWhiteSpace(time)) body["time"] = time;
        return CallAsync(svc, "ha_set_input_datetime", "input_datetime", "set_datetime", entityId, body, ct);
    }

    private static async Task<string> CallAsync(
        HomeAssistantService svc,
        string toolName,
        string domain,
        string service,
        string entityId,
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        if (!svc.Options.EnableShortcuts) throw new InvalidOperationException("Shortcut tools are disabled.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed(toolName);
        svc.EnsureEntityAllowed(entityId);

        body["entity_id"] = entityId;
        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }
}
