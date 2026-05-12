using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Lightweight discovery helpers derived from documented REST endpoints
/// (/api/states and /api/services). These reduce token cost vs. dumping
/// the full state list every time the agent needs to find an entity or service.
/// </summary>
[McpServerToolType]
public static class DiscoveryTools
{
    [McpServerTool(Name = "ha_list_domains"),
     Description("List every entity domain Home Assistant currently knows about, with a count of entities in each. Cheap one-shot overview derived from GET /api/states.")]
    public static async Task<string> ListDomains(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var id)) continue;
            var s = id.GetString();
            if (string.IsNullOrEmpty(s)) continue;
            var dot = s.IndexOf('.');
            if (dot <= 0) continue;
            var domain = s[..dot];
            counts[domain] = counts.GetValueOrDefault(domain) + 1;
        }
        return JsonOpts.Serialize(
            counts.OrderByDescending(kv => kv.Value)
                  .Select(kv => new { domain = kv.Key, count = kv.Value }));
    }

    [McpServerTool(Name = "ha_list_entities"),
     Description("List entity_id + friendly_name only (no other attributes). Much cheaper than ha_list_states for entity discovery. Optional domain and substring filters.")]
    public static async Task<string> ListEntities(
        HomeAssistantService svc,
        [Description("Optional entity domain filter, e.g. 'light'.")] string? domain = null,
        [Description("Optional substring filter against entity_id or friendly_name (case-insensitive).")] string? contains = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var max = Math.Max(1, svc.Options.MaxStatesReturned);
        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            if (domain is not null && !id.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null;
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                && attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                friendly = fn.GetString();
            }
            if (contains is not null
                && !id.Contains(contains, StringComparison.OrdinalIgnoreCase)
                && !(friendly?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            rows.Add(new { entity_id = id, friendly_name = friendly, state });
            if (rows.Count >= max) break;
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_search_entities"),
     Description("Search entities by free-text query against entity_id, friendly_name, and other text attributes. Returns the same projection as ha_list_entities.")]
    public static async Task<string> SearchEntities(
        HomeAssistantService svc,
        [Description("Search query — matched case-insensitively against entity_id, friendly_name, and string attributes.")] string query,
        [Description("Optional domain filter, e.g. 'light'.")] string? domain = null,
        [Description("Maximum rows to return. Capped by MaxStatesReturned.")] int? limit = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.", nameof(query));

        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var cap = Math.Min(limit ?? svc.Options.MaxStatesReturned, svc.Options.MaxStatesReturned);
        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            if (domain is not null && !id.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null;
            bool matched = id.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                {
                    friendly = fn.GetString();
                    if (!matched && (friendly?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                        matched = true;
                }
                if (!matched)
                {
                    foreach (var prop in attrs.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String &&
                            (prop.Value.GetString()?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                        {
                            matched = true;
                            break;
                        }
                    }
                }
            }
            if (!matched) continue;
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            rows.Add(new { entity_id = id, friendly_name = friendly, state });
            if (rows.Count >= cap) break;
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_get_service_schema"),
     Description("Get the field schema and description for a single service (e.g. light.turn_on). Derived from GET /api/services so the agent doesn't have to download every service.")]
    public static async Task<string> GetServiceSchema(
        HomeAssistantService svc,
        [Description("Service domain, e.g. 'light'.")] string domain,
        [Description("Service name within that domain, e.g. 'turn_on'.")] string service,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        var json = await svc.GetJsonAsync("api/services", ct);
        if (json.ValueKind != JsonValueKind.Array)
            return JsonOpts.Serialize(new { error = "Unexpected /api/services response." });

        foreach (var entry in json.EnumerateArray())
        {
            if (!entry.TryGetProperty("domain", out var d) || !string.Equals(d.GetString(), domain, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var prop in services.EnumerateObject())
            {
                if (string.Equals(prop.Name, service, StringComparison.OrdinalIgnoreCase))
                    return JsonOpts.Serialize(new { domain, service = prop.Name, schema = prop.Value });
            }
        }
        return JsonOpts.Serialize(new { error = $"Service '{domain}.{service}' not found." });
    }
}
