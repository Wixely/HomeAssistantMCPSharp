# HomeAssistantMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **Home Assistant** over Streamable HTTP.

Exposes **105 tools** across state, services, history, registry (areas / floors / labels), media + TTS, automations / scripts / scenes, presence, weather, energy, device health, sun, input helpers, notifications, dashboard YAML editing, and guarded custom-card/integration development.

## Highlights

- HTTP MCP server using the Streamable HTTP transport.
- **Read-only by default** — every write tool is blocked until `HomeAssistant:ReadOnly` is set to `false`.
- Per-feature toggles (`EnableStates`, `EnableServices`, `EnableHistory`, `EnableLogbook`, `EnableTemplate`, `EnableConversation`, `EnableCalendar`, `EnableDiagnostics`, `EnableEvents`, `EnableRegistry`, `EnableShortcuts`, `EnableNotifications`, `EnableEnergy`, `EnableDashboardYaml`); plus `EnableCameraSnapshot` (off by default).
- Entity and domain allow/deny lists.
- Home Assistant Lovelace dashboard config can be read as YAML through an MCP resource (`homeassistant://dashboard-yaml`) and edited through write-gated tools.
- Optional development tools inspect, stage, preview, deploy, verify, and roll back custom cards and integrations through a scoped local or SFTP configuration root.
- Serilog logging to console **and** rolling files (daily, 50 MB rollover, 14-file retention).
- Runs as a Docker container, a Windows Service, or a console app.
- Registry data (areas, floors, labels) is fetched through Home Assistant templates because the registry has no direct REST route.

## MCP tools exposed

### Core REST coverage

| Tool | Underlying HA route | Read/Write |
| --- | --- | --- |
| `ha_ping` | `GET /api/` | read |
| `ha_get_config` | `GET /api/config` | read |
| `ha_list_states` | `GET /api/states` | read |
| `ha_get_state` | `GET /api/states/<entity_id>` | read |
| `ha_set_state` | `POST /api/states/<entity_id>` | write |
| `ha_delete_state` | `DELETE /api/states/<entity_id>` | write |
| `ha_list_services` | `GET /api/services` | read |
| `ha_call_service` | `POST /api/services/<domain>/<service>` | write |
| `ha_check_config` | `POST /api/config/core/check_config` | write |
| `ha_history` | `GET /api/history/period/<start>` | read |
| `ha_logbook` | `GET /api/logbook/<start>` | read |
| `ha_list_event_types` | `GET /api/events` | read |
| `ha_fire_event` | `POST /api/events/<event_type>` | write |
| `ha_render_template` | `POST /api/template` | read |
| `ha_list_calendars` | `GET /api/calendars` | read |
| `ha_calendar_events` | `GET /api/calendars/<entity_id>` | read |
| `ha_conversation_process` | `POST /api/conversation/process` | write |
| `ha_intent_handle` | `POST /api/intent/handle` | write |
| `ha_error_log` | `GET /api/error_log` | read |
| `ha_server_info` | _(local)_ | read |

### Dashboard YAML (Home Assistant Lovelace)

| Tool / resource | Notes |
| --- | --- |
| `homeassistant://dashboard-yaml` | MCP resource containing the default Lovelace dashboard config rendered as YAML. |
| `ha_list_dashboards` | Lists Lovelace dashboards and their `url_path` values so clients know what to pass as `urlPath`. |
| `ha_get_dashboard_yaml_info` | Returns dashboard URL path, resource URI, content size, and SHA-256. |
| `ha_get_dashboard_yaml` | Reads a Lovelace dashboard config through Home Assistant's WebSocket API and renders it as YAML. |
| `ha_update_dashboard_yaml` | Parses YAML and saves it through `lovelace/config/save`. Requires `ReadOnly=false`. |
| `ha_replace_dashboard_yaml_text` | Replaces exact text in the rendered YAML, parses it, then saves through Home Assistant. Requires `ReadOnly=false`; rejects ambiguous single replacements. |

These tools do not read or write local files. They use Home Assistant's `/api/websocket` Lovelace commands. Call `ha_list_dashboards` first, then pass a returned `url_path` as `urlPath` for a non-default dashboard, such as `lovelace-music`. Omit `urlPath` for the default dashboard. If Home Assistant reports the dashboard is YAML-mode and cannot be saved through the UI/WebSocket API, reads will still work but writes will be rejected by Home Assistant.

### Custom card and integration development

Development tools are disabled by default. They expose only `www/<package>` and `custom_components/<domain>` through a configured local or SFTP transport. They do not provide general filesystem, SSH, shell, archive extraction, `.storage`, or YAML configuration access.

| Tool | Purpose |
| --- | --- |
| `ha_dev_get_environment` | Report Home Assistant health, transport capability, gates, and transfer limits. |
| `ha_dev_list_custom_cards` / `ha_dev_get_custom_card` | Inspect deployed card files, hashes, and Lovelace resources. |
| `ha_dev_list_custom_integrations` / `ha_dev_get_custom_integration` | Inspect integration manifests, files, config entries, and bounded matching logs. |
| `ha_dev_begin_deployment` | Start a time-limited `custom_card` or `custom_integration` staging session. |
| `ha_dev_put_deployment_file` | Upload one ordered Base64 chunk with final length and SHA-256 verification. |
| `ha_dev_preview_deployment` | Validate the package, compare target hashes, and issue a short-lived apply token. |
| `ha_dev_apply_deployment` | Recheck the preview, save a rollback snapshot, replace files, and register or validate the result. |
| `ha_dev_verify_deployment` | Verify served card bytes or return integration config entries and bounded logs. |
| `ha_dev_preview_rollback` / `ha_dev_rollback_deployment` | Refuse stale targets, then restore the saved snapshot with a separate token. |
| `ha_dev_prepare_restart` / `ha_dev_restart_and_verify` | Confirm, restart, wait for Core health, and verify an integration deployment. |
| `ha_dev_list_deployments` / `ha_dev_get_deployment` | Inspect persisted deployment state without returning staged or backup contents. |

Card deployment writes a module beneath `www`, then creates or updates its storage-mode Lovelace resource with a content-hash query value. Integration deployment checks `manifest.json`, JSON syntax, file types, package size, and hashes before writing. It then calls `POST /api/config/core/check_config`. Python changes are reported as restart-required; the deployment tool does not claim hot reload.

Each mutation requires `HomeAssistant:ReadOnly=false`, `Development:EnableInspection=true`, and `Development:EnableDeployment=true`. Restart also requires `Development:EnableRestart=true`. Apply, rollback, and restart use short-lived action-specific confirmation tokens. A rollback is refused when files have changed since apply.

### Discovery & search (cheap projections over `/api/states` and `/api/services`)

| Tool | Description |
| --- | --- |
| `ha_list_domains` | Unique domains with entity counts. |
| `ha_list_entities` | `entity_id` + `friendly_name` only — much cheaper than `ha_list_states`. |
| `ha_search_entities` | Free-text search across `entity_id`, `friendly_name`, and string attributes. |
| `ha_get_service_schema` | Field schema for one service instead of dumping all services. |

### Registry (template-backed — fills the REST API gap)

| Tool | Backed by |
| --- | --- |
| `ha_list_areas` | `areas()` + `area_name()` + `floor_id()` |
| `ha_list_floors` | `floors()` + `floor_name()` |
| `ha_list_labels` | `labels()` + `label_name()` |
| `ha_entities_in_area` | `area_entities(area)` |
| `ha_devices_in_area` | `area_devices(area)` |
| `ha_areas_on_floor` | `floor_areas(floor)` |
| `ha_entity_area` | `area_id()` + `area_name()` (reverse lookup) |

### Typed action shortcuts (all writes; gated by `ReadOnly` + `EnableShortcuts`)

| Tool | Wraps |
| --- | --- |
| `ha_turn_on` / `ha_turn_off` / `ha_toggle` | `homeassistant.turn_on/off/toggle` (cross-domain) |
| `ha_set_light` | `light.turn_on` with typed brightness / color temp / RGB / transition |
| `ha_set_climate_temperature` / `ha_set_climate_mode` | `climate.set_temperature` / `climate.set_hvac_mode` |
| `ha_open_cover` / `ha_close_cover` / `ha_set_cover_position` | `cover.*` |
| `ha_lock` / `ha_unlock` | `lock.lock` / `lock.unlock` |
| `ha_notify` | `notify.<service>` (default `notify.notify`) |

### Media & TTS

| Tool | Notes |
| --- | --- |
| `ha_list_media_players` | Lists every media_player with title/artist/volume/source. Optional state filter. |
| `ha_get_media_player` | Full single-entity state including `source_list` and `sound_mode_list`. |
| `ha_list_media_sources` | Just the available `source_list` / `sound_mode_list` for one player. |
| `ha_media_play_pause` / `ha_media_stop` / `ha_media_next` / `ha_media_previous` | `media_player.*` transport. |
| `ha_media_volume` / `ha_media_select_source` | Volume + input source. |
| `ha_play_media` | `media_player.play_media` — URL, `media-source://...` URI, or integration-specific id (e.g. Spotify URI). Supports `enqueue` and `announce`. |
| `ha_list_tts_engines` | Lists every `tts.*` entity with default language and supported voices. |
| `ha_speak` | `tts.speak` — speaks `message` on a media_player using a chosen TTS engine. |

> Browsing a media library (`media_player.browse_media`) is WebSocket-only in Home Assistant — not exposed here. Use `ha_play_media` with a known content id, or surface library entries via the HA UI / a Jinja template.

### Automations, scripts, scenes

| Tool | Notes |
| --- | --- |
| `ha_list_automations` / `ha_list_scripts` / `ha_list_scenes` | Filtered projections of `/api/states`. |
| `ha_enable_automation` / `ha_disable_automation` / `ha_trigger_automation` | `automation.turn_on` / `turn_off` / `trigger`. |
| `ha_run_script` | `script.turn_on` with optional variables. |
| `ha_activate_scene` | `scene.turn_on`. |

### Persistent notifications

| Tool | Wraps |
| --- | --- |
| `ha_list_persistent_notifications` | `/api/states` filtered to `persistent_notification.*` |
| `ha_create_notification` | `persistent_notification.create` |
| `ha_dismiss_notification` | `persistent_notification.dismiss` |

### Verification & overview

| Tool | Description |
| --- | --- |
| `ha_summary` | One-shot situational summary: domain counts, lights/switches on, climate setpoints, unlocked locks, unavailable entities. |
| `ha_wait_for_state` | Poll an entity until it reaches an expected state or the timeout elapses. |

### Presence — who's home

| Tool | Description |
| --- | --- |
| `ha_list_people` | Every `person.*` entity with state, source device, GPS coordinates, last_changed. |
| `ha_list_zones` | Every `zone.*` with lat/lon/radius and the persons currently inside. |
| `ha_who_is_home` | Quick "who is home?" — counts and names of `person.*` entities in state `home`. |

### Weather

| Tool | Description |
| --- | --- |
| `ha_list_weather` | Every `weather.*` entity with current condition, temperature, humidity, pressure, wind, visibility. |
| `ha_get_forecast` | Calls `weather.get_forecasts` with `return_response=true`. Pass `forecastType` = `daily` / `hourly` / `twice_daily`. Requires HA ≥ 2024.8 for REST service-response support. |

### Device health

| Tool | Description |
| --- | --- |
| `ha_list_batteries` | Every battery-level entity (sorted lowest-first), with a `low` flag at or below `LowBatteryThresholdPct`. Optional `onlyLow=true`. |
| `ha_list_unavailable` | Every entity reporting `unavailable` or `unknown`, grouped by domain. Diagnostic complement to `ha_summary`. |

### Sun

| Tool | Description |
| --- | --- |
| `ha_get_sun` | `sun.sun` state plus elevation, azimuth, and next dawn/dusk/rising/setting/noon/midnight. |

### Input helpers (writes)

| Tool | Wraps |
| --- | --- |
| `ha_set_input_number` | `input_number.set_value` |
| `ha_set_input_text` | `input_text.set_value` |
| `ha_set_input_select` | `input_select.select_option` |
| `ha_set_input_datetime` | `input_datetime.set_datetime` (datetime / date / time) |

### Electricity / energy

| Tool | Description |
| --- | --- |
| `ha_list_energy_sensors` | Every electricity-related sensor (power, energy, voltage, current, frequency, monetary), classified by category. Derived from `/api/states` via `device_class` + unit heuristics. |
| `ha_get_energy_summary` | One-shot dashboard: total live power draw, per-sensor power readings, today's cumulative energy totals, voltage/current readings, active electricity cost trackers. |
| `ha_get_energy_history` | History for a single energy/power sensor over N hours, with computed min/max/avg/last and a delta when `state_class=total_increasing`. |

> The Home Assistant **Energy dashboard preferences** and **long-term statistics** (`statistics_during_period`, `energy/get_prefs`, `energy/fossil_energy_consumption`) are WebSocket-only and not reachable from REST. These tools work off the same raw sensor data the Energy dashboard consumes.

### Camera (off by default)

| Tool | Notes |
| --- | --- |
| `ha_camera_snapshot` | `camera.snapshot` — writes a file on the HA host. Gated by `EnableCameraSnapshot`. |

## Running

### Docker (recommended)

Run the container:

```sh
docker run -d --name hamcp \
  -p 5703:5703 \
  -e HAMCP_HomeAssistant__BaseUrl=http://homeassistant.local:8123/ \
  -e HAMCP_HomeAssistant__AccessToken=YOUR_LONG_LIVED_TOKEN \
  -e HAMCP_HomeAssistant__ReadOnly=true \
  -e HAMCP_HomeAssistant__AllowedEntities__0=light.kitchen \
  -e HAMCP_Server__Password=change-me \
  ghcr.io/wixely/homeassistantmcpsharp:latest
```

Or build locally with the bundled `docker-compose.yml`:

```sh
docker compose up --build -d
```

Either way, point your MCP client at `http://<host>:5703/mcp`.

### Standalone (console)

```sh
dotnet run
```

### Windows Service

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\HomeAssistantMCPSharp

sc.exe create HomeAssistantMCPSharp `
    binPath= "C:\Services\HomeAssistantMCPSharp\HomeAssistantMCPSharp.exe" `
    start= auto `
    DisplayName= "Home Assistant MCP (C#)"
sc.exe description HomeAssistantMCPSharp "MCP server for Home Assistant."
sc.exe start HomeAssistantMCPSharp
```

## Configuration

Configure via one of:

- `HomeAssistantMCPSharp.json` — committed defaults.
- `HomeAssistantMCPSharp.Local.json` — gitignored local overrides (loaded automatically). Plaintext secrets in this file are encrypted on startup by default.
- Environment variables — prefix `HAMCP_`, with `__` as the section separator (e.g. `HAMCP_HomeAssistant__AccessToken`).
- Command-line arguments.

Environment variables override JSON. Arrays use numeric indexes, for example `HAMCP_HomeAssistant__AllowedEntities__0=light.kitchen`. Booleans use `true` or `false`.

Create a long-lived access token in Home Assistant under your **profile → Security → Long-Lived Access Tokens**.

| Setting | Default | Description |
| --- | --- | --- |
| `HomeAssistant:BaseUrl` | `http://homeassistant.local:8123/` | URL of the HA instance. |
| `HomeAssistant:AccessToken` | _(none)_ | Long-lived access token. |
| `HomeAssistant:UserAgent` | `HomeAssistantMCPSharp` | User-Agent header sent to HA. |
| `HomeAssistant:RequestTimeoutSeconds` | `30` | HTTP request timeout. |
| `HomeAssistant:ReadOnly` | **`true`** | Disables every write tool when true. |
| `HomeAssistant:IgnoreCertificateErrors` | `false` | Skip TLS validation for self-signed HA. |
| `HomeAssistant:AllowedEntities` / `BlockedEntities` | `[]` | Entity allow/deny lists. |
| `HomeAssistant:AllowedDomains` / `BlockedDomains` | `[]` | Domain allow/deny lists. |
| `HomeAssistant:MaxStatesReturned` | `500` | Cap on `ha_list_states` rows. |
| `HomeAssistant:MaxHistoryEntries` | `1000` | Cap on `ha_history` rows per entity. |
| `HomeAssistant:DefaultHistoryHours` | `24` | Default `ha_history` window. |
| `HomeAssistant:DefaultLogbookHours` | `24` | Default `ha_logbook` window. |
| `HomeAssistant:AttributeValueTruncate` | `512` | Truncate state-attribute strings longer than this in `ha_list_states` output. |
| `HomeAssistant:ConversationLanguage` | `en` | Default language for `ha_conversation_process`. |
| `HomeAssistant:Enable*` | `true` | Per-feature tool toggles: `EnableStates`, `EnableServices`, `EnableHistory`, `EnableLogbook`, `EnableTemplate`, `EnableConversation`, `EnableCalendar`, `EnableDiagnostics`, `EnableEvents`, `EnableRegistry`, `EnableShortcuts`, `EnableNotifications`, `EnableEnergy`, `EnableDashboardYaml`. |
| `HomeAssistant:LowBatteryThresholdPct` | `20` | Battery percentage at or below which `ha_list_batteries` flags an entity as `low`. |
| `HomeAssistant:EnableCameraSnapshot` | `false` | Off by default — `camera.snapshot` writes a file on the HA host. |
| `HomeAssistant:WaitForStatePollSeconds` | `1` | Poll interval used by `ha_wait_for_state`. |
| `HomeAssistant:WaitForStateMaxSeconds` | `60` | Hard cap on `ha_wait_for_state` timeout (caller value is clamped). |
| `Server:Host` / `Port` / `Path` | `localhost` / `5703` / `/mcp` | HTTP bind details. Use `0.0.0.0` inside Docker. |
| `Server:WindowsServiceName` | `HomeAssistantMCPSharp` | SCM service name. |
| `Server:Password` | blank | Optional MCP endpoint password; blank disables password auth. |
| `Development:EnableInspection` | `false` | Expose read-only development inventory and deployment records. |
| `Development:EnableDeployment` | `false` | Allow staged apply and rollback when Home Assistant write mode is also enabled. |
| `Development:EnableRestart` | `false` | Allow separately confirmed Core restarts after integration deployment. |
| `Development:Transport` | `None` | `None`, `Local`, or `Sftp`. |
| `Development:LocalConfigRoot` | blank | Existing Home Assistant configuration directory for the local transport. |
| `Development:DataPath` | `data/deployments` | Persistent staging, metadata, and rollback snapshots. Keep this outside Home Assistant's configuration tree. |
| `Development:MaxChunkBytes` | `262144` | Maximum decoded upload chunk. |
| `Development:MaxFileBytes` | `8388608` | Maximum staged file. |
| `Development:MaxPackageBytes` | `33554432` | Maximum complete package and rollback snapshot. |
| `Development:Sftp:*` | blank | SFTP host, port, username, credential, required SHA-256 host-key fingerprint, and absolute configuration root. |
| `SecretProtection:CredentialProtection` | `auto` | `dpapi-user` on Windows and `aesgcm-keyfile` elsewhere. Also accepts `dpapi-machine`, `aesgcm-keyfile`, or `none`. |
| `SecretProtection:AutoProtectCredentials` | `true` | Encrypt plaintext secrets found in the configured local file and rewrite it atomically. |
| `SecretProtection:KeyFilePath` | `data/master.key` | AES-GCM key file. Back it up and keep it separate from the protected configuration. |

When `Server:Password` is set, MCP requests must provide the password as `Authorization: Bearer <password>`, the Basic auth password, or `X-MCP-Password`.

### Encrypted local secrets

You can put `HomeAssistant:AccessToken`, `Server:Password`, `Development:Sftp:Password`, or `Development:Sftp:PrivateKeyPassphrase` in `HomeAssistantMCPSharp.Local.json` as plaintext for the first startup. The server encrypts each value, removes the plaintext property, writes a matching `*Protected` value and `*ProtectionScheme`, then continues using the decrypted value in memory. The rewrite is atomic, although JSON comments and custom formatting are normalized.

On Windows, `auto` uses DPAPI for the current account. A Windows Service must run as the account that first encrypted the file; alternatively, select `dpapi-machine` before the first startup. Linux, macOS, and containers use AES-256-GCM with `data/master.key`. The `data` directory is already persistent in the supplied Docker configuration. Back up the key: losing it makes the protected values unrecoverable.

Environment variables and command-line values are runtime overrides. They are not copied into the local file or encrypted there. Set `SecretProtection:CredentialProtection` to `none` only when you intentionally want to keep file values as plaintext.

## Read-only mode

Read-only is **on by default**. To enable write tools, set `HomeAssistant:ReadOnly=false`. With write enabled, MCP clients can call any tool flagged as a write — anything that actuates a device, calls a service, sets or deletes a state, fires an event, runs an automation / script / scene, plays media, speaks TTS, creates notifications, mutates an input helper, edits the configured dashboard YAML, or invokes the conversation/intent pipeline.

For finer-grained control:

- Use **`AllowedEntities` / `BlockedEntities`** and **`AllowedDomains` / `BlockedDomains`** to restrict which entities and domains can be touched (applies to both reads and writes).
- Use the **`Enable*` toggles** to turn off entire tool categories regardless of read/write mode.
- `EnableDashboardYaml` exposes Lovelace dashboard config through Home Assistant's WebSocket API; it does not grant filesystem access.
- `EnableCameraSnapshot` is off by default because `camera.snapshot` writes a file on the HA host's filesystem.
- Development deployment has three independent gates and a separate scoped transport. Leave all three disabled on instances that only need normal Home Assistant management.

### Development transport examples

For a container with an intentionally mounted Home Assistant configuration directory:

```yaml
environment:
  HAMCP_HomeAssistant__ReadOnly: "false"
  HAMCP_Development__EnableInspection: "true"
  HAMCP_Development__EnableDeployment: "true"
  HAMCP_Development__Transport: "Local"
  HAMCP_Development__LocalConfigRoot: "/homeassistant"
volumes:
  - hamcp-development-data:/app/data
  - /path/to/homeassistant/config:/homeassistant
```

For SFTP, keep the password or private-key passphrase in an environment variable or the encrypted `HomeAssistantMCPSharp.Local.json`. Pin the server's SHA-256 host-key fingerprint; the client refuses an unpinned or changed key.

```text
HAMCP_Development__Transport=Sftp
HAMCP_Development__Sftp__Host=ha.example.com
HAMCP_Development__Sftp__Username=hamcp-deployer
HAMCP_Development__Sftp__Password=<password>
HAMCP_Development__Sftp__HostKeySha256=<sha256-host-key-fingerprint-in-hex>
HAMCP_Development__Sftp__ConfigRoot=/srv/homeassistant/config
```

Give the deployment account access only to the intended `www` and `custom_components` trees when the host permits it. Keep the configured root accurate because the bridge applies its path restrictions relative to that root.
