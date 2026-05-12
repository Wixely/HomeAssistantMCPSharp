namespace HomeAssistantMCPSharp.Configuration;

public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    /// <summary>Base URL of the Home Assistant instance (e.g. http://homeassistant.local:8123/).</summary>
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123/";

    /// <summary>Long-lived access token created in your HA user profile under Security.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>User-Agent header sent to Home Assistant.</summary>
    public string UserAgent { get; set; } = "HomeAssistantMCPSharp";

    /// <summary>When true, all write/service-call/state-set tools are disabled. Default true.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Skip TLS validation. Useful for self-signed HA installs. Off by default.</summary>
    public bool IgnoreCertificateErrors { get; set; } = false;

    /// <summary>Optional allow-list of entity_ids (exact match). Empty = no restriction.</summary>
    public List<string> AllowedEntities { get; set; } = new();

    /// <summary>Optional deny-list of entity_ids (exact match). Evaluated after AllowedEntities.</summary>
    public List<string> BlockedEntities { get; set; } = new();

    /// <summary>Optional allow-list of entity domains (e.g. "light", "switch"). Empty = no restriction.</summary>
    public List<string> AllowedDomains { get; set; } = new();

    /// <summary>Optional deny-list of entity domains. Evaluated after AllowedDomains.</summary>
    public List<string> BlockedDomains { get; set; } = new();

    /// <summary>Maximum number of state entries returned by list_states. Guards against huge payloads.</summary>
    public int MaxStatesReturned { get; set; } = 500;

    /// <summary>Maximum number of history entries returned per entity per request.</summary>
    public int MaxHistoryEntries { get; set; } = 1000;

    /// <summary>Default history window in hours when no explicit range is supplied.</summary>
    public int DefaultHistoryHours { get; set; } = 24;

    /// <summary>Default logbook window in hours when no explicit range is supplied.</summary>
    public int DefaultLogbookHours { get; set; } = 24;

    /// <summary>Truncate state attribute values longer than this (chars) in list_states output.</summary>
    public int AttributeValueTruncate { get; set; } = 512;

    /// <summary>Expose tools that read state, config, and diagnostics.</summary>
    public bool EnableStates { get; set; } = true;

    /// <summary>Expose tools that call services / fire events / set state.</summary>
    public bool EnableServices { get; set; } = true;

    /// <summary>Expose history tools.</summary>
    public bool EnableHistory { get; set; } = true;

    /// <summary>Expose logbook tools.</summary>
    public bool EnableLogbook { get; set; } = true;

    /// <summary>Expose template render tool.</summary>
    public bool EnableTemplate { get; set; } = true;

    /// <summary>Expose conversation/intent tools.</summary>
    public bool EnableConversation { get; set; } = true;

    /// <summary>Expose calendar tools.</summary>
    public bool EnableCalendar { get; set; } = true;

    /// <summary>Expose tools that read error logs and diagnostics.</summary>
    public bool EnableDiagnostics { get; set; } = true;

    /// <summary>Expose event firing tools (write operation).</summary>
    public bool EnableEvents { get; set; } = true;

    /// <summary>Expose registry tools (areas, floors, labels, entities-in-area) backed by template rendering.</summary>
    public bool EnableRegistry { get; set; } = true;

    /// <summary>Expose typed action shortcuts (turn_on/off, set_light, set_climate, media, lock, notify, ...).</summary>
    public bool EnableShortcuts { get; set; } = true;

    /// <summary>Expose persistent_notification create/dismiss/list tools.</summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>Expose electricity / energy sensor helpers (list, summary, history).</summary>
    public bool EnableEnergy { get; set; } = true;

    /// <summary>Battery percentage at or below which ha_list_batteries flags an entity as 'low'.</summary>
    public int LowBatteryThresholdPct { get; set; } = 20;

    /// <summary>Expose camera.snapshot. Off by default — writes a file on the HA host's filesystem.</summary>
    public bool EnableCameraSnapshot { get; set; } = false;

    /// <summary>How often ha_wait_for_state polls the entity state, in seconds.</summary>
    public int WaitForStatePollSeconds { get; set; } = 1;

    /// <summary>Maximum wait window for ha_wait_for_state, in seconds. Caller-supplied values are clamped to this.</summary>
    public int WaitForStateMaxSeconds { get; set; } = 60;

    /// <summary>Default language used for conversation/process intents.</summary>
    public string ConversationLanguage { get; set; } = "en";
}

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5100;
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name when running as a Windows Service.</summary>
    public string WindowsServiceName { get; set; } = "HomeAssistantMCPSharp";
}
