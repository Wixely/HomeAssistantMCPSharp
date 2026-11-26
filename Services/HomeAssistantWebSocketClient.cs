using System.Net.WebSockets;
using System.Text.Json;
using HomeAssistantMCPSharp.Configuration;
using Microsoft.Extensions.Options;

namespace HomeAssistantMCPSharp.Services;

/// <summary>Bounded request/response client for Home Assistant's authenticated WebSocket API.</summary>
public sealed class HomeAssistantWebSocketClient
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private readonly HomeAssistantOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HomeAssistantWebSocketClient(IOptions<HomeAssistantOptions> options)
    {
        _options = options.Value;
    }

    public async Task<JsonElement> SendCommandAsync(
        IReadOnlyDictionary<string, object?> command,
        CancellationToken ct = default)
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

        await SendJsonAsync(socket, new { type = "auth", access_token = _options.AccessToken }, ct);
        var auth = await ReceiveJsonAsync(socket, ct);
        if (IsMessageType(auth, "auth_invalid"))
        {
            var message = auth.TryGetProperty("message", out var value) ? value.GetString() : "invalid access token";
            throw new InvalidOperationException($"Home Assistant WebSocket authentication failed: {message}");
        }

        if (!IsMessageType(auth, "auth_ok"))
        {
            throw new InvalidOperationException("Home Assistant WebSocket authentication did not complete.");
        }

        var request = new Dictionary<string, object?>(command, StringComparer.Ordinal)
        {
            ["id"] = 1,
        };
        await SendJsonAsync(socket, request, ct);

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

    private static async Task SendJsonAsync(ClientWebSocket socket, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"Home Assistant WebSocket request exceeds {MaxMessageBytes} bytes.");
        }

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
            if (stream.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException($"Home Assistant WebSocket response exceeds {MaxMessageBytes} bytes.");
            }

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
            return "unknown error";
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return error.ToString();
        }

        var code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
        var text = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
        return string.IsNullOrWhiteSpace(code) ? text ?? "unknown error" : $"{code}: {text}";
    }
}
