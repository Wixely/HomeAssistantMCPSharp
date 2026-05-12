using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Higher-level read-only helpers that aggregate or poll the documented
/// REST API: a one-shot home summary and a state-change verifier.
/// </summary>
[McpServerToolType]
public static class OverviewTools
{
    [McpServerTool(Name = "ha_summary"),
     Description("Return a one-shot situational-awareness summary: counts by domain, lights/switches that are on, climate setpoints, unlocked locks, and unavailable entities. Aggregated from GET /api/states.")]
    public static async Task<string> Summary(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var domainCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lightsOn = new List<object>();
        var switchesOn = new List<object>();
        var unlocked = new List<object>();
        var unavailable = new List<string>();
        var climates = new List<object>();
        int total = 0;

        foreach (var el in json.EnumerateArray())
        {
            total++;
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (string.IsNullOrEmpty(entityId)) continue;
            var dot = entityId.IndexOf('.');
            if (dot <= 0) continue;
            var domain = entityId[..dot];
            domainCounts[domain] = domainCounts.GetValueOrDefault(domain) + 1;

            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            string? friendly = null;
            JsonElement attrs = default;
            bool hasAttrs = el.TryGetProperty("attributes", out attrs) && attrs.ValueKind == JsonValueKind.Object;
            if (hasAttrs && attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                friendly = fn.GetString();

            if (string.Equals(state, "unavailable", StringComparison.OrdinalIgnoreCase))
            {
                unavailable.Add(entityId);
                continue;
            }

            if (string.Equals(domain, "light", StringComparison.OrdinalIgnoreCase) && string.Equals(state, "on", StringComparison.OrdinalIgnoreCase))
            {
                int? bri = null;
                if (hasAttrs && attrs.TryGetProperty("brightness", out var b) && b.ValueKind == JsonValueKind.Number)
                    bri = b.GetInt32();
                lightsOn.Add(new { entity_id = entityId, friendly_name = friendly, brightness = bri });
            }
            else if (string.Equals(domain, "switch", StringComparison.OrdinalIgnoreCase) && string.Equals(state, "on", StringComparison.OrdinalIgnoreCase))
            {
                switchesOn.Add(new { entity_id = entityId, friendly_name = friendly });
            }
            else if (string.Equals(domain, "lock", StringComparison.OrdinalIgnoreCase) && !string.Equals(state, "locked", StringComparison.OrdinalIgnoreCase))
            {
                unlocked.Add(new { entity_id = entityId, friendly_name = friendly, state });
            }
            else if (string.Equals(domain, "climate", StringComparison.OrdinalIgnoreCase))
            {
                double? temp = null, target = null;
                if (hasAttrs)
                {
                    if (attrs.TryGetProperty("current_temperature", out var ct1) && ct1.ValueKind == JsonValueKind.Number) temp = ct1.GetDouble();
                    if (attrs.TryGetProperty("temperature", out var tt) && tt.ValueKind == JsonValueKind.Number) target = tt.GetDouble();
                }
                climates.Add(new { entity_id = entityId, friendly_name = friendly, mode = state, current_temperature = temp, target_temperature = target });
            }
        }

        var summary = new
        {
            total_entities = total,
            domains = domainCounts.OrderByDescending(kv => kv.Value).Select(kv => new { domain = kv.Key, count = kv.Value }),
            lights_on = lightsOn,
            switches_on = switchesOn,
            locks_unlocked = unlocked,
            climates,
            unavailable_count = unavailable.Count,
            unavailable_sample = unavailable.Take(10),
        };
        return JsonOpts.Serialize(summary);
    }

    [McpServerTool(Name = "ha_wait_for_state"),
     Description("Poll an entity until its state matches expectedState (case-insensitive) or the timeout elapses. Useful for verifying that a service call took effect. Read-only.")]
    public static async Task<string> WaitForState(
        HomeAssistantService svc,
        [Description("Entity id to poll, e.g. 'light.kitchen'.")] string entityId,
        [Description("Expected state string, e.g. 'on', 'off', 'locked'.")] string expectedState,
        [Description("Maximum seconds to wait. Clamped by HomeAssistant:WaitForStateMaxSeconds.")] int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        svc.EnsureEntityAllowed(entityId);

        var maxWait = Math.Max(1, svc.Options.WaitForStateMaxSeconds);
        var wait = Math.Clamp(timeoutSeconds ?? maxWait, 1, maxWait);
        var pollDelay = TimeSpan.FromSeconds(Math.Max(1, svc.Options.WaitForStatePollSeconds));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(wait);
        var path = $"api/states/{Uri.EscapeDataString(entityId)}";

        JsonElement last = default;
        string? lastState = null;
        while (true)
        {
            last = await svc.GetJsonAsync(path, ct);
            lastState = last.ValueKind == JsonValueKind.Object && last.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (string.Equals(lastState, expectedState, StringComparison.OrdinalIgnoreCase))
            {
                return JsonOpts.Serialize(new { matched = true, state = lastState, entity = last });
            }
            if (DateTimeOffset.UtcNow >= deadline) break;
            try { await Task.Delay(pollDelay, ct); }
            catch (TaskCanceledException) { break; }
        }
        return JsonOpts.Serialize(new { matched = false, state = lastState, entity = last, waited_seconds = wait });
    }
}
