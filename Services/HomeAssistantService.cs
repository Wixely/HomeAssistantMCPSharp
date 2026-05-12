using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Options;

namespace HomeAssistantMCPSharp.Services;

/// <summary>
/// Thin wrapper around the documented Home Assistant REST API.
/// All routes used here are described in https://developers.home-assistant.io/docs/api/rest/ —
/// no scraped or undocumented endpoints.
/// </summary>
public sealed class HomeAssistantService
{
    private readonly HttpClient _http;
    private readonly HomeAssistantOptions _options;

    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HomeAssistantService(HttpClient http, IOptions<HomeAssistantOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public HomeAssistantOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;
    public Uri? BaseAddress => _http.BaseAddress;

    public void EnsureWriteAllowed(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set HomeAssistant:ReadOnly=false to allow writes.");
        }
    }

    public void EnsureEntityAllowed(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("entity_id is required.", nameof(entityId));

        var dot = entityId.IndexOf('.');
        var domain = dot > 0 ? entityId[..dot] : entityId;

        if (_options.AllowedDomains.Count > 0 &&
            !_options.AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Domain '{domain}' is not in AllowedDomains.");
        }
        if (_options.BlockedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Domain '{domain}' is in BlockedDomains.");
        }
        if (_options.AllowedEntities.Count > 0 &&
            !_options.AllowedEntities.Contains(entityId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Entity '{entityId}' is not in AllowedEntities.");
        }
        if (_options.BlockedEntities.Contains(entityId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Entity '{entityId}' is in BlockedEntities.");
        }
    }

    public void EnsureDomainAllowed(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("domain is required.", nameof(domain));
        if (_options.AllowedDomains.Count > 0 &&
            !_options.AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Domain '{domain}' is not in AllowedDomains.");
        }
        if (_options.BlockedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Domain '{domain}' is in BlockedDomains.");
        }
    }

    // GET helpers --------------------------------------------------------

    public Task<JsonElement> GetJsonAsync(string path, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, content: null, ct);

    public Task<string> GetStringAsync(string path, CancellationToken ct = default)
        => SendRawAsync(HttpMethod.Get, path, content: null, ct);

    // POST helpers -------------------------------------------------------

    public Task<JsonElement> PostJsonAsync(string path, object? body, CancellationToken ct = default)
    {
        var content = body is null
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : (HttpContent)JsonContent.Create(body);
        return SendAsync(HttpMethod.Post, path, content, ct);
    }

    public Task<JsonElement> DeleteJsonAsync(string path, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, path, content: null, ct);

    // Core send ---------------------------------------------------------

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var raw = await SendRawAsync(method, path, content, ct);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Some endpoints (e.g. /api/template, /api/error_log) return plain text.
            // Wrap into a JSON value so callers can always serialise the result.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { text = raw }, JsonRead));
            return doc.RootElement.Clone();
        }
    }

    private async Task<string> SendRawAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path) { Content = content };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = $"Home Assistant returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {method} {path}";
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                msg += " — check HomeAssistant:AccessToken";
            if (!string.IsNullOrWhiteSpace(body)) msg += $": {Truncate(body, 1024)}";
            throw new HttpRequestException(msg);
        }
        return body;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…(truncated)";
}
