using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Media-player and text-to-speech tools.
/// Listing / state inspection is derived from GET /api/states (the documented
/// media_player attributes). Transport and play_media go via POST /api/services/media_player/*.
/// TTS uses POST /api/services/tts/speak.
///
/// Note: browsing a media library (media_player.browse_media) is a WebSocket-only
/// command in Home Assistant — it isn't reachable from the REST API, so this tool
/// surface is limited to listing players, inspecting their state, and playing a
/// known media_content_id / URL.
/// </summary>
[McpServerToolType]
public static class MediaTools
{
    // ---- listing & inspection ------------------------------------------

    [McpServerTool(Name = "ha_list_media_players"),
     Description("List every media_player entity with current state, friendly name, currently-playing title/artist, volume, source, and source_list. Derived from GET /api/states.")]
    public static async Task<string> ListMediaPlayers(
        HomeAssistantService svc,
        [Description("Optional state filter: 'playing', 'paused', 'idle', 'on', 'off'. Case-insensitive.")] string? stateFilter = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null || !entityId.StartsWith("media_player.", StringComparison.OrdinalIgnoreCase)) continue;

            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (stateFilter is not null && !string.Equals(state, stateFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(ProjectMediaPlayer(entityId, state, el));
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_get_media_player"),
     Description("Get the detailed state of a single media_player entity — what's playing, volume, source, supported features.")]
    public static async Task<string> GetMediaPlayer(
        HomeAssistantService svc,
        [Description("media_player entity id, e.g. 'media_player.living_room'.")] string entityId,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        svc.EnsureEntityAllowed(entityId);
        var json = await svc.GetJsonAsync($"api/states/{Uri.EscapeDataString(entityId)}", ct);
        var state = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("state", out var s) ? s.GetString() : null;
        return JsonOpts.Serialize(ProjectMediaPlayer(entityId, state, json, full: true));
    }

    [McpServerTool(Name = "ha_list_media_sources"),
     Description("Return the available input sources and sound modes for a media_player (source_list and sound_mode_list attributes).")]
    public static async Task<string> ListMediaSources(
        HomeAssistantService svc,
        [Description("media_player entity id.")] string entityId,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        svc.EnsureEntityAllowed(entityId);
        var json = await svc.GetJsonAsync($"api/states/{Uri.EscapeDataString(entityId)}", ct);

        string[] sources = Array.Empty<string>();
        string[] soundModes = Array.Empty<string>();
        string? currentSource = null;
        string? currentSoundMode = null;
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            sources = ReadStringArray(attrs, "source_list");
            soundModes = ReadStringArray(attrs, "sound_mode_list");
            if (attrs.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String) currentSource = src.GetString();
            if (attrs.TryGetProperty("sound_mode", out var sm) && sm.ValueKind == JsonValueKind.String) currentSoundMode = sm.GetString();
        }
        return JsonOpts.Serialize(new
        {
            entity_id = entityId,
            current_source = currentSource,
            sources,
            current_sound_mode = currentSoundMode,
            sound_modes = soundModes,
        });
    }

    // ---- transport -----------------------------------------------------

    [McpServerTool(Name = "ha_media_play_pause"),
     Description("Toggle play/pause on a media_player entity. Calls media_player.media_play_pause. Requires write mode.")]
    public static Task<string> MediaPlayPause(HomeAssistantService svc, [Description("media_player entity id.")] string entityId, CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_media_play_pause", "media_player", "media_play_pause", entityId, null, ct);

    [McpServerTool(Name = "ha_media_stop"),
     Description("Stop playback on a media_player. Calls media_player.media_stop. Requires write mode.")]
    public static Task<string> MediaStop(HomeAssistantService svc, [Description("media_player entity id.")] string entityId, CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_media_stop", "media_player", "media_stop", entityId, null, ct);

    [McpServerTool(Name = "ha_media_next"),
     Description("Skip to the next track on a media_player. Calls media_player.media_next_track. Requires write mode.")]
    public static Task<string> MediaNext(HomeAssistantService svc, [Description("media_player entity id.")] string entityId, CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_media_next", "media_player", "media_next_track", entityId, null, ct);

    [McpServerTool(Name = "ha_media_previous"),
     Description("Go to the previous track on a media_player. Calls media_player.media_previous_track. Requires write mode.")]
    public static Task<string> MediaPrevious(HomeAssistantService svc, [Description("media_player entity id.")] string entityId, CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_media_previous", "media_player", "media_previous_track", entityId, null, ct);

    [McpServerTool(Name = "ha_media_volume"),
     Description("Set a media_player's volume 0.0-1.0. Calls media_player.volume_set. Requires write mode.")]
    public static Task<string> MediaVolume(
        HomeAssistantService svc,
        [Description("media_player entity id.")] string entityId,
        [Description("Volume level 0.0 (mute) to 1.0 (max).")] double volumeLevel,
        CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_media_volume", "media_player", "volume_set", entityId,
            new Dictionary<string, object?> { ["volume_level"] = Math.Clamp(volumeLevel, 0.0, 1.0) }, ct);

    [McpServerTool(Name = "ha_media_select_source"),
     Description("Select an input source on a media_player. Calls media_player.select_source. Requires write mode.")]
    public static Task<string> MediaSelectSource(
        HomeAssistantService svc,
        [Description("media_player entity id.")] string entityId,
        [Description("Source name (must match the entity's available source_list).")] string source,
        CancellationToken ct = default)
        => CallEntityServiceAsync(svc, "ha_media_select_source", "media_player", "select_source", entityId,
            new Dictionary<string, object?> { ["source"] = source }, ct);

    // ---- play_media ----------------------------------------------------

    [McpServerTool(Name = "ha_play_media"),
     Description("Play a specific media item on a media_player. Calls media_player.play_media. " +
                 "media_content_id is a URL ('https://...'), a Home Assistant media-source URI ('media-source://media_source/local/song.mp3'), " +
                 "or an integration-specific id (e.g. Spotify URI). media_content_type is one of 'music', 'video', 'tvshow', 'episode', 'channel', 'playlist', 'url', etc. " +
                 "Requires write mode.")]
    public static async Task<string> PlayMedia(
        HomeAssistantService svc,
        [Description("media_player entity id, e.g. 'media_player.living_room'.")] string entityId,
        [Description("Media content id — URL, media-source URI, or integration-specific id.")] string mediaContentId,
        [Description("Media content type, e.g. 'music', 'video', 'playlist', 'url'.")] string mediaContentType,
        [Description("Optional enqueue mode: 'play' (default), 'next', 'add', 'replace'.")] string? enqueue = null,
        [Description("Optional announce flag — interrupts current playback for a short clip when supported.")] bool? announce = null,
        [Description("Optional extra JSON of integration-specific parameters merged into the service-data.")] string? extraJson = null,
        CancellationToken ct = default)
    {
        EnsureWriteAndServices(svc, "ha_play_media");
        svc.EnsureEntityAllowed(entityId);

        var body = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["media_content_id"] = mediaContentId,
            ["media_content_type"] = mediaContentType,
        };
        if (!string.IsNullOrWhiteSpace(enqueue)) body["enqueue"] = enqueue;
        if (announce.HasValue) body["announce"] = announce.Value;
        if (!string.IsNullOrWhiteSpace(extraJson))
        {
            using var doc = JsonDocument.Parse(extraJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
                body[prop.Name] = prop.Value.Clone();
        }
        var result = await svc.PostJsonAsync("api/services/media_player/play_media", body, ct);
        return JsonOpts.Serialize(result);
    }

    // ---- TTS -----------------------------------------------------------

    [McpServerTool(Name = "ha_list_tts_engines"),
     Description("List configured TTS engines (entities in the 'tts' domain) with their default language and available voices/options where exposed. Derived from GET /api/states.")]
    public static async Task<string> ListTtsEngines(HomeAssistantService svc, CancellationToken ct = default)
    {
        if (!svc.Options.EnableStates) throw new InvalidOperationException("State tools are disabled.");
        var json = await svc.GetJsonAsync("api/states", ct);
        if (json.ValueKind != JsonValueKind.Array) return JsonOpts.Serialize(json);

        var rows = new List<object>();
        foreach (var el in json.EnumerateArray())
        {
            if (!el.TryGetProperty("entity_id", out var idEl)) continue;
            var entityId = idEl.GetString();
            if (entityId is null || !entityId.StartsWith("tts.", StringComparison.OrdinalIgnoreCase)) continue;

            string? friendly = null, defaultLang = null;
            string[] supportedLanguages = Array.Empty<string>();
            string[] voices = Array.Empty<string>();
            if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
                if (attrs.TryGetProperty("default_language", out var dl) && dl.ValueKind == JsonValueKind.String) defaultLang = dl.GetString();
                supportedLanguages = ReadStringArray(attrs, "supported_languages");
                voices = ReadStringArray(attrs, "supported_voices");
            }
            var state = el.TryGetProperty("state", out var s) ? s.GetString() : null;
            rows.Add(new
            {
                entity_id = entityId,
                friendly_name = friendly,
                state,
                default_language = defaultLang,
                supported_languages = supportedLanguages,
                supported_voices = voices,
            });
        }
        return JsonOpts.Serialize(rows);
    }

    [McpServerTool(Name = "ha_speak"),
     Description("Speak text through a media_player using a TTS engine. Calls tts.speak (modern HA TTS action). " +
                 "Use ha_list_tts_engines to find a tts.* entity to use as the engine. Requires write mode.")]
    public static async Task<string> Speak(
        HomeAssistantService svc,
        [Description("TTS engine entity id, e.g. 'tts.google_translate_en_com' or 'tts.home_assistant_cloud'.")] string ttsEntityId,
        [Description("Target media_player entity id, e.g. 'media_player.living_room'.")] string mediaPlayerEntityId,
        [Description("The text to speak.")] string message,
        [Description("Optional language code, e.g. 'en', 'en-GB'. Defaults to the engine's configured language.")] string? language = null,
        [Description("Whether HA should cache the generated audio. Defaults to true.")] bool cache = true,
        [Description("Optional engine-specific options JSON, e.g. '{\"voice\":\"...\"}'.")] string? optionsJson = null,
        CancellationToken ct = default)
    {
        EnsureWriteAndServices(svc, "ha_speak");
        svc.EnsureEntityAllowed(ttsEntityId);
        svc.EnsureEntityAllowed(mediaPlayerEntityId);
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required.", nameof(message));

        var body = new Dictionary<string, object?>
        {
            ["entity_id"] = ttsEntityId,
            ["media_player_entity_id"] = mediaPlayerEntityId,
            ["message"] = message,
            ["cache"] = cache,
        };
        if (!string.IsNullOrWhiteSpace(language)) body["language"] = language;
        if (!string.IsNullOrWhiteSpace(optionsJson))
        {
            using var doc = JsonDocument.Parse(optionsJson);
            body["options"] = doc.RootElement.Clone();
        }
        var result = await svc.PostJsonAsync("api/services/tts/speak", body, ct);
        return JsonOpts.Serialize(result);
    }

    // ---- helpers -------------------------------------------------------

    private static object ProjectMediaPlayer(string entityId, string? state, JsonElement el, bool full = false)
    {
        string? friendly = null;
        string? mediaTitle = null, mediaArtist = null, mediaAlbum = null, mediaContentType = null, mediaContentId = null;
        string? source = null, soundMode = null;
        double? volume = null;
        bool? muted = null;
        double? duration = null, position = null;
        int? supportedFeatures = null;
        string[] sourceList = Array.Empty<string>();
        string[] soundModeList = Array.Empty<string>();

        if (el.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("friendly_name", out var fn) && fn.ValueKind == JsonValueKind.String) friendly = fn.GetString();
            if (attrs.TryGetProperty("media_title", out var mt) && mt.ValueKind == JsonValueKind.String) mediaTitle = mt.GetString();
            if (attrs.TryGetProperty("media_artist", out var ma) && ma.ValueKind == JsonValueKind.String) mediaArtist = ma.GetString();
            if (attrs.TryGetProperty("media_album_name", out var mal) && mal.ValueKind == JsonValueKind.String) mediaAlbum = mal.GetString();
            if (attrs.TryGetProperty("media_content_type", out var mct) && mct.ValueKind == JsonValueKind.String) mediaContentType = mct.GetString();
            if (attrs.TryGetProperty("media_content_id", out var mci) && mci.ValueKind == JsonValueKind.String) mediaContentId = mci.GetString();
            if (attrs.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String) source = src.GetString();
            if (attrs.TryGetProperty("sound_mode", out var sm) && sm.ValueKind == JsonValueKind.String) soundMode = sm.GetString();
            if (attrs.TryGetProperty("volume_level", out var v) && v.ValueKind == JsonValueKind.Number) volume = v.GetDouble();
            if (attrs.TryGetProperty("is_volume_muted", out var im) && (im.ValueKind == JsonValueKind.True || im.ValueKind == JsonValueKind.False)) muted = im.GetBoolean();
            if (attrs.TryGetProperty("media_duration", out var d) && d.ValueKind == JsonValueKind.Number) duration = d.GetDouble();
            if (attrs.TryGetProperty("media_position", out var p) && p.ValueKind == JsonValueKind.Number) position = p.GetDouble();
            if (attrs.TryGetProperty("supported_features", out var sf) && sf.ValueKind == JsonValueKind.Number) supportedFeatures = sf.GetInt32();
            if (full)
            {
                sourceList = ReadStringArray(attrs, "source_list");
                soundModeList = ReadStringArray(attrs, "sound_mode_list");
            }
        }
        return full
            ? new
            {
                entity_id = entityId,
                friendly_name = friendly,
                state,
                volume_level = volume,
                muted,
                source,
                source_list = sourceList,
                sound_mode = soundMode,
                sound_mode_list = soundModeList,
                media_title = mediaTitle,
                media_artist = mediaArtist,
                media_album = mediaAlbum,
                media_duration = duration,
                media_position = position,
                media_content_type = mediaContentType,
                media_content_id = mediaContentId,
                supported_features = supportedFeatures,
            }
            : (object)new
            {
                entity_id = entityId,
                friendly_name = friendly,
                state,
                volume_level = volume,
                source,
                media_title = mediaTitle,
                media_artist = mediaArtist,
            };
    }

    private static string[] ReadStringArray(JsonElement attrs, string name)
    {
        if (!attrs.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list.ToArray();
    }

    private static async Task<string> CallEntityServiceAsync(
        HomeAssistantService svc,
        string toolName,
        string domain,
        string service,
        string entityId,
        Dictionary<string, object?>? body,
        CancellationToken ct)
    {
        EnsureWriteAndServices(svc, toolName);
        svc.EnsureEntityAllowed(entityId);

        body ??= new Dictionary<string, object?>();
        body["entity_id"] = entityId;
        var path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";
        var result = await svc.PostJsonAsync(path, body, ct);
        return JsonOpts.Serialize(result);
    }

    private static void EnsureWriteAndServices(HomeAssistantService svc, string toolName)
    {
        if (!svc.Options.EnableShortcuts) throw new InvalidOperationException("Shortcut tools are disabled.");
        if (!svc.Options.EnableServices) throw new InvalidOperationException("Service tools are disabled.");
        svc.EnsureWriteAllowed(toolName);
    }
}
