using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeAssistantMCPSharp.Configuration;
using HomeAssistantMCPSharp.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HomeAssistantMCPSharp.Tests;

public sealed class DevelopmentDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hamcp-deployment-" + Guid.NewGuid().ToString("N"));

    public DevelopmentDeploymentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Integration_deployment_applies_and_rolls_back_with_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var configRoot = Path.Combine(_root, "homeassistant");
        var existingRoot = Path.Combine(configRoot, "custom_components", "sample_bridge");
        Directory.CreateDirectory(existingRoot);
        await File.WriteAllTextAsync(Path.Combine(existingRoot, "manifest.json"), Manifest("0.9.0"), ct);
        await File.WriteAllTextAsync(Path.Combine(existingRoot, "__init__.py"), "OLD = True", ct);

        var homeOptions = Options.Create(new HomeAssistantOptions
        {
            BaseUrl = "http://homeassistant.test/",
            ReadOnly = false,
        });
        var developmentOptions = Options.Create(new DevelopmentOptions
        {
            EnableInspection = true,
            EnableDeployment = true,
            Transport = "Local",
            LocalConfigRoot = configRoot,
            DataPath = Path.Combine(_root, "deployments"),
        });
        var home = new HomeAssistantService(new HttpClient(new HomeAssistantHandler())
        {
            BaseAddress = new Uri("http://homeassistant.test/"),
        }, homeOptions);
        var webSocket = new HomeAssistantWebSocketClient(homeOptions);
        var service = new HomeAssistantDevelopmentService(
            developmentOptions,
            homeOptions,
            new ConfigRootTransportFactory(developmentOptions),
            home,
            webSocket,
            new TestHostEnvironment(_root));

        var begun = Json(await service.BeginDeploymentAsync(
            "custom_integration", "sample_bridge", null, removeMissingFiles: true, ct));
        var id = begun.GetProperty("Id").GetString()!;
        await UploadAsync(service, id, "manifest.json", Manifest("1.0.0"), ct);
        await UploadAsync(service, id, "__init__.py", "NEW = True", ct);

        var preview = Json(await service.PreviewDeploymentAsync(id, ct));
        var applyToken = preview.GetProperty("confirmation").GetProperty("Token").GetString()!;
        await service.ApplyDeploymentAsync(id, applyToken, ct);

        Assert.Contains("1.0.0", await File.ReadAllTextAsync(Path.Combine(existingRoot, "manifest.json"), ct));
        Assert.Equal("NEW = True", await File.ReadAllTextAsync(Path.Combine(existingRoot, "__init__.py"), ct));

        var rollbackPreview = Json(await service.PreviewRollbackAsync(id, ct));
        var rollbackToken = rollbackPreview.GetProperty("confirmation").GetProperty("Token").GetString()!;
        await service.RollbackAsync(id, rollbackToken, ct);

        Assert.Contains("0.9.0", await File.ReadAllTextAsync(Path.Combine(existingRoot, "manifest.json"), ct));
        Assert.Equal("OLD = True", await File.ReadAllTextAsync(Path.Combine(existingRoot, "__init__.py"), ct));
    }

    private static async Task UploadAsync(
        HomeAssistantDevelopmentService service,
        string id,
        string path,
        string content,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await service.PutFileAsync(id, path, 0, Convert.ToBase64String(bytes), true, bytes.Length, sha, ct);
    }

    private static JsonElement Json(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static string Manifest(string version) =>
        $$"""{"domain":"sample_bridge","name":"Sample bridge","version":"{{version}}","documentation":"https://example.invalid","codeowners":["@example"]}""";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class HomeAssistantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri?.AbsolutePath switch
            {
                "/api/config/core/check_config" => "{\"result\":\"valid\",\"errors\":null,\"warnings\":null}",
                "/api/core/state" => "{\"state\":\"RUNNING\"}",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "HomeAssistantMCPSharp.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
