using System.ComponentModel;
using System.Text.Json;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

/// <summary>
/// Wraps POST /api/conversation/process and POST /api/intent/handle — documented HA REST API.
/// </summary>
[McpServerToolType]
public static class ConversationTools
{
    [McpServerTool(Name = "ha_conversation_process"),
     Description("Send a natural-language utterance to the HA conversation agent. Calls POST /api/conversation/process. Acts as a write because the agent can call services. Requires write mode.")]
    public static async Task<string> ConversationProcess(
        HomeAssistantService svc,
        [Description("The natural-language text to process, e.g. 'turn on the kitchen light'.")] string text,
        [Description("Optional conversation_id for multi-turn context.")] string? conversationId = null,
        [Description("Optional language override. Defaults to HomeAssistant:ConversationLanguage.")] string? language = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableConversation) throw new InvalidOperationException("Conversation tools are disabled.");
        svc.EnsureWriteAllowed("ha_conversation_process");

        var body = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["language"] = string.IsNullOrWhiteSpace(language) ? svc.Options.ConversationLanguage : language,
        };
        if (!string.IsNullOrWhiteSpace(conversationId)) body["conversation_id"] = conversationId;

        var result = await svc.PostJsonAsync("api/conversation/process", body, ct);
        return JsonOpts.Serialize(result);
    }

    [McpServerTool(Name = "ha_intent_handle"),
     Description("Handle a single named intent. Calls POST /api/intent/handle. Requires the intent component and write mode.")]
    public static async Task<string> IntentHandle(
        HomeAssistantService svc,
        [Description("Intent name, e.g. 'HassTurnOn'.")] string name,
        [Description("Optional intent slots JSON, e.g. '{\"name\":{\"value\":\"kitchen light\"}}'.")] string? dataJson = null,
        CancellationToken ct = default)
    {
        if (!svc.Options.EnableConversation) throw new InvalidOperationException("Conversation tools are disabled.");
        svc.EnsureWriteAllowed("ha_intent_handle");

        var body = new Dictionary<string, object?> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            using var doc = JsonDocument.Parse(dataJson);
            body["data"] = doc.RootElement.Clone();
        }
        var result = await svc.PostJsonAsync("api/intent/handle", body, ct);
        return JsonOpts.Serialize(result);
    }
}
