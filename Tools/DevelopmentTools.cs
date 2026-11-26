using System.ComponentModel;
using HomeAssistantMCPSharp.Services;
using ModelContextProtocol.Server;

namespace HomeAssistantMCPSharp.Tools;

[McpServerToolType]
public static class DevelopmentTools
{
    [McpServerTool(Name = "ha_dev_get_environment"),
     Description("Inspect Home Assistant and the configured custom-card/integration deployment capability. Read-only and disabled by default.")]
    public static async Task<string> GetEnvironment(HomeAssistantDevelopmentService development, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.GetEnvironmentAsync(ct));

    [McpServerTool(Name = "ha_dev_list_custom_cards"),
     Description("List custom-card packages deployed under Home Assistant's www directory and their Lovelace resources.")]
    public static async Task<string> ListCustomCards(HomeAssistantDevelopmentService development, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.ListCustomCardsAsync(ct));

    [McpServerTool(Name = "ha_dev_get_custom_card"),
     Description("Inspect one deployed custom-card package, including file hashes and matching Lovelace resources.")]
    public static async Task<string> GetCustomCard(
        HomeAssistantDevelopmentService development,
        [Description("Package directory beneath www.")] string packageName,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.GetCustomCardAsync(packageName, ct));

    [McpServerTool(Name = "ha_dev_list_custom_integrations"),
     Description("List custom integrations deployed under custom_components with manifest and config-entry summaries.")]
    public static async Task<string> ListCustomIntegrations(HomeAssistantDevelopmentService development, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.ListCustomIntegrationsAsync(ct));

    [McpServerTool(Name = "ha_dev_get_custom_integration"),
     Description("Inspect one custom integration, including manifest, files, config entries, and optional bounded domain logs.")]
    public static async Task<string> GetCustomIntegration(
        HomeAssistantDevelopmentService development,
        [Description("Integration domain and custom_components directory name.")] string domain,
        [Description("Include bounded matching lines from Home Assistant's error log.")] bool includeLogs = false,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.GetCustomIntegrationAsync(domain, includeLogs, ct));

    [McpServerTool(Name = "ha_dev_begin_deployment"),
     Description("Begin a bounded custom_card or custom_integration deployment session. Requires development deployment and write mode.")]
    public static async Task<string> BeginDeployment(
        HomeAssistantDevelopmentService development,
        [Description("custom_card or custom_integration.")] string kind,
        [Description("Target package directory name.")] string packageName,
        [Description("Card module path within the package. Required for custom cards.")] string? entryFile = null,
        [Description("Remove live files omitted from the package. False preserves them.")] bool removeMissingFiles = false,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.BeginDeploymentAsync(kind, packageName, entryFile, removeMissingFiles, ct));

    [McpServerTool(Name = "ha_dev_put_deployment_file"),
     Description("Upload the next Base64 chunk of a staged deployment file. Chunks must be ordered by exact byte offset.")]
    public static async Task<string> PutDeploymentFile(
        HomeAssistantDevelopmentService development,
        string deploymentId,
        [Description("Normalized file path inside the package.")] string relativePath,
        [Description("Expected current byte length of the staged file.")] long offset,
        [Description("Base64-encoded chunk, bounded by the configured chunk limit.")] string contentBase64,
        [Description("Finalize and hash-check this file after appending the chunk.")] bool isFinal = false,
        [Description("Required on the final chunk.")] long? finalLength = null,
        [Description("Required lowercase or uppercase SHA-256 on the final chunk.")] string? finalSha256 = null,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.PutFileAsync(deploymentId, relativePath, offset, contentBase64, isFinal, finalLength, finalSha256, ct));

    [McpServerTool(Name = "ha_dev_preview_deployment"),
     Description("Validate a staged package, diff it against Home Assistant, and issue a short-lived apply token.")]
    public static async Task<string> PreviewDeployment(HomeAssistantDevelopmentService development, string deploymentId, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.PreviewDeploymentAsync(deploymentId, ct));

    [McpServerTool(Name = "ha_dev_apply_deployment"),
     Description("Apply a freshly previewed deployment, retain a rollback snapshot, and register or validate it.")]
    public static async Task<string> ApplyDeployment(
        HomeAssistantDevelopmentService development,
        string deploymentId,
        [Description("Single-use token returned by ha_dev_preview_deployment.")] string confirmationToken,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.ApplyDeploymentAsync(deploymentId, confirmationToken, ct));

    [McpServerTool(Name = "ha_dev_verify_deployment"),
     Description("Verify that Home Assistant serves a deployed card or report an integration's config entries and bounded logs.")]
    public static async Task<string> VerifyDeployment(HomeAssistantDevelopmentService development, string deploymentId, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.VerifyDeploymentAsync(deploymentId, ct));

    [McpServerTool(Name = "ha_dev_preview_rollback"),
     Description("Check that an applied deployment is unchanged and issue a short-lived rollback token.")]
    public static async Task<string> PreviewRollback(HomeAssistantDevelopmentService development, string deploymentId, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.PreviewRollbackAsync(deploymentId, ct));

    [McpServerTool(Name = "ha_dev_rollback_deployment"),
     Description("Restore the rollback snapshot for an unchanged deployment target.")]
    public static async Task<string> RollbackDeployment(
        HomeAssistantDevelopmentService development,
        string deploymentId,
        [Description("Single-use token returned by ha_dev_preview_rollback.")] string confirmationToken,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.RollbackAsync(deploymentId, confirmationToken, ct));

    [McpServerTool(Name = "ha_dev_prepare_restart"),
     Description("Read current Core health and issue a short-lived restart token for an applied integration deployment.")]
    public static async Task<string> PrepareRestart(HomeAssistantDevelopmentService development, string deploymentId, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.PrepareRestartAsync(deploymentId, ct));

    [McpServerTool(Name = "ha_dev_restart_and_verify"),
     Description("Restart Home Assistant with a single-use token, wait for Core to recover, then verify the integration deployment.")]
    public static async Task<string> RestartAndVerify(
        HomeAssistantDevelopmentService development,
        string deploymentId,
        [Description("Single-use token returned by ha_dev_prepare_restart.")] string confirmationToken,
        CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.RestartAndVerifyAsync(deploymentId, confirmationToken, ct));

    [McpServerTool(Name = "ha_dev_list_deployments"),
     Description("List persisted development deployments without returning staged or backup file contents.")]
    public static async Task<string> ListDeployments(HomeAssistantDevelopmentService development, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.ListDeploymentsAsync(ct));

    [McpServerTool(Name = "ha_dev_get_deployment"),
     Description("Get one development deployment's state, hashes, preview, and verification metadata.")]
    public static async Task<string> GetDeployment(HomeAssistantDevelopmentService development, string deploymentId, CancellationToken ct = default) =>
        JsonOpts.Serialize(await development.GetDeploymentAsync(deploymentId, ct));
}
