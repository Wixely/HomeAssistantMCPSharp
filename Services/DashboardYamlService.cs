using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeAssistantMCPSharp.Services;

public sealed class DashboardYamlService
{
    private readonly HomeAssistantOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .Build();

    public const string ResourceUri = "homeassistant://dashboard-yaml";

    public DashboardYamlService(IOptions<HomeAssistantOptions> options)
    {
        _options = options.Value;
    }

    public async Task<DashboardYamlSnapshot> ReadAsync(string? urlPath = null, bool force = false, CancellationToken ct = default)
    {
        EnsureEnabled();

        var command = new Dictionary<string, object?>
        {
            ["type"] = "lovelace/config",
            ["force"] = force,
        };
        if (!string.IsNullOrWhiteSpace(urlPath))
        {
            command["url_path"] = urlPath;
        }

        var config = await SendWebSocketCommandAsync(command, ct);
        var normalized = NormalizeJson(config);
        var yaml = YamlSerializer.Serialize(normalized);

        return new DashboardYamlSnapshot(
            string.IsNullOrWhiteSpace(urlPath) ? null : urlPath,
            yaml,
            ComputeSha256(yaml));
    }

    public async Task<DashboardYamlInfo> GetInfoAsync(string? urlPath = null, CancellationToken ct = default)
    {
        var snapshot = await ReadAsync(urlPath, force: false, ct);
        return new DashboardYamlInfo(
            snapshot.UrlPath,
            ResourceUri,
            snapshot.Sha256,
            Encoding.UTF8.GetByteCount(snapshot.Content));
    }

    public async Task<object?> ListDashboardsAsync(CancellationToken ct = default)
    {
        EnsureEnabled();

        var result = await SendWebSocketCommandAsync(
            new Dictionary<string, object?> { ["type"] = "lovelace/dashboards/list" },
            ct);

        return NormalizeJson(result);
    }

    public async Task<DashboardYamlWriteResult> WriteAsync(
        string content,
        string? urlPath,
        string? expectedSha256,
        CancellationToken ct = default)
    {
        EnsureWriteAllowed("ha_update_dashboard_yaml");

        var current = await ReadAsync(urlPath, force: true, ct);
        EnsureExpectedSha(current.Sha256, expectedSha256);

        var yamlObject = YamlDeserializer.Deserialize<object?>(content)
            ?? throw new InvalidOperationException("Dashboard YAML must contain a mapping/object.");
        var normalized = NormalizeYaml(yamlObject);

        if (normalized is not IReadOnlyDictionary<string, object?>)
        {
            throw new InvalidOperationException("Dashboard YAML root must be a mapping/object.");
        }

        var config = JsonSerializer.SerializeToElement(normalized, JsonOptions);
        var command = new Dictionary<string, object?>
        {
            ["type"] = "lovelace/config/save",
            ["config"] = config,
        };
        if (!string.IsNullOrWhiteSpace(urlPath))
        {
            command["url_path"] = urlPath;
        }

        await SendWebSocketCommandAsync(command, ct);

        var written = await ReadAsync(urlPath, force: true, ct);
        return new DashboardYamlWriteResult(
            written.UrlPath,
            current.Sha256,
            written.Sha256,
            Encoding.UTF8.GetByteCount(written.Content));
    }

    public async Task<DashboardYamlReplaceResult> ReplaceAsync(
        string oldText,
        string newText,
        bool replaceAll,
        string? urlPath,
        string? expectedSha256,
        CancellationToken ct = default)
    {
        EnsureWriteAllowed("ha_replace_dashboard_yaml_text");

        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("oldText is required.", nameof(oldText));
        }

        var snapshot = await ReadAsync(urlPath, force: true, ct);
        EnsureExpectedSha(snapshot.Sha256, expectedSha256);

        var count = CountOccurrences(snapshot.Content, oldText);
        if (count == 0)
        {
            throw new InvalidOperationException("oldText was not found in the dashboard YAML.");
        }

        if (!replaceAll && count > 1)
        {
            throw new InvalidOperationException(
                $"oldText occurs {count} times. Set replaceAll=true or provide a more specific oldText.");
        }

        var updated = replaceAll
            ? snapshot.Content.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(snapshot.Content, oldText, newText);

        var write = await WriteAsync(updated, urlPath, snapshot.Sha256, ct);
        return new DashboardYamlReplaceResult(
            write.UrlPath,
            snapshot.Sha256,
            write.Sha256,
            write.Length,
            replaceAll ? count : 1);
    }

    private async Task<JsonElement> SendWebSocketCommandAsync(Dictionary<string, object?> command, CancellationToken ct)
    {
        var baseUri = new Uri(string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? "http://homeassistant.local:8123/"
            : _options.BaseUrl);

        var builder = new UriBuilder(baseUri)
        {
            Scheme = string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "api/websocket",
            Query = string.Empty,
        };

        using var socket = new ClientWebSocket();
        if (_options.IgnoreCertificateErrors)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        await socket.ConnectAsync(builder.Uri, ct);

        var authRequired = await ReceiveJsonAsync(socket, ct);
        if (!IsMessageType(authRequired, "auth_required"))
        {
            throw new InvalidOperationException("Home Assistant WebSocket did not request authentication.");
        }

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new InvalidOperationException("HomeAssistant:AccessToken is required for Home Assistant WebSocket API access.");
        }

        await SendJsonAsync(socket, new
        {
            type = "auth",
            access_token = _options.AccessToken,
        }, ct);

        var auth = await ReceiveJsonAsync(socket, ct);
        if (IsMessageType(auth, "auth_invalid"))
        {
            var message = auth.TryGetProperty("message", out var authMessage) ? authMessage.GetString() : "invalid access token";
            throw new InvalidOperationException($"Home Assistant WebSocket authentication failed: {message}");
        }
        if (!IsMessageType(auth, "auth_ok"))
        {
            throw new InvalidOperationException("Home Assistant WebSocket authentication did not complete.");
        }

        command["id"] = 1;
        await SendJsonAsync(socket, command, ct);

        while (true)
        {
            var message = await ReceiveJsonAsync(socket, ct);
            if (!message.TryGetProperty("id", out var id) || id.GetInt32() != 1)
            {
                continue;
            }

            if (!message.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                throw new InvalidOperationException($"Home Assistant WebSocket command failed: {ReadError(message)}");
            }

            return message.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.EnableDashboardYaml)
        {
            throw new InvalidOperationException("Dashboard YAML tools are disabled.");
        }
    }

    private void EnsureWriteAllowed(string operation)
    {
        EnsureEnabled();
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' is blocked: server is running in read-only mode. " +
                "Set HomeAssistant:ReadOnly=false to allow dashboard YAML writes.");
        }
    }

    private static void EnsureExpectedSha(string currentSha256, string? expectedSha256)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(currentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Dashboard YAML changed since it was read. Expected SHA-256 {expectedSha256}, current SHA-256 {currentSha256}.");
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Home Assistant closed the WebSocket connection.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static bool IsMessageType(JsonElement message, string type) =>
        message.TryGetProperty("type", out var messageType) &&
        string.Equals(messageType.GetString(), type, StringComparison.OrdinalIgnoreCase);

    private static string ReadError(JsonElement message)
    {
        if (!message.TryGetProperty("error", out var error))
        {
            return JsonSerializer.Serialize(message, JsonOptions);
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            var errMessage = error.TryGetProperty("message", out var messageEl) ? messageEl.GetString() : null;
            return string.IsNullOrWhiteSpace(code) ? errMessage ?? JsonSerializer.Serialize(error, JsonOptions) : $"{code}: {errMessage}";
        }

        return JsonSerializer.Serialize(error, JsonOptions);
    }

    private static object? NormalizeJson(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => NormalizeJson(prop.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJson).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };

    private static object? NormalizeYaml(object? value) =>
        value switch
        {
            null => null,
            IDictionary<object, object> dict => dict.ToDictionary(
                pair => Convert.ToString(pair.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                pair => NormalizeYaml(pair.Value),
                StringComparer.Ordinal),
            IEnumerable<object?> list when value is not string => list.Select(NormalizeYaml).ToList(),
            _ => value,
        };

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? content
            : string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
    }
}

public sealed record DashboardYamlSnapshot(
    string? UrlPath,
    string Content,
    string Sha256);

public sealed record DashboardYamlInfo(
    string? UrlPath,
    string ResourceUri,
    string Sha256,
    int Length);

public sealed record DashboardYamlWriteResult(
    string? UrlPath,
    string PreviousSha256,
    string Sha256,
    int Length);

public sealed record DashboardYamlReplaceResult(
    string? UrlPath,
    string PreviousSha256,
    string Sha256,
    int Length,
    int Replacements);
