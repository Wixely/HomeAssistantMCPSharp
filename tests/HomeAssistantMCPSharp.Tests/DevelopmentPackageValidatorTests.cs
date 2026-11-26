using System.Text;
using HomeAssistantMCPSharp.Services;

namespace HomeAssistantMCPSharp.Tests;

public sealed class DevelopmentPackageValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hamcp-validator-" + Guid.NewGuid().ToString("N"));

    public DevelopmentPackageValidatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Validates_minimal_custom_integration()
    {
        var ct = TestContext.Current.CancellationToken;
        var manifest = """
            {"domain":"sample_bridge","name":"Sample bridge","version":"1.0.0","documentation":"https://example.invalid","codeowners":["@example"]}
            """;
        await File.WriteAllTextAsync(Path.Combine(_root, "manifest.json"), manifest, Encoding.UTF8, ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "__init__.py"), string.Empty, Encoding.UTF8, ct);

        await DevelopmentPackageValidator.ValidateAsync(Deployment("sample_bridge"), _root, ct);
    }

    [Fact]
    public async Task Rejects_manifest_domain_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var manifest = """
            {"domain":"another_domain","name":"Sample bridge","version":"1.0.0","documentation":"https://example.invalid","codeowners":["@example"]}
            """;
        await File.WriteAllTextAsync(Path.Combine(_root, "manifest.json"), manifest, Encoding.UTF8, ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "__init__.py"), string.Empty, Encoding.UTF8, ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentPackageValidator.ValidateAsync(Deployment("sample_bridge"), _root, ct));
    }

    private static DevelopmentDeployment Deployment(string package) => new()
    {
        Kind = "custom_integration",
        PackageName = package,
        Files = new Dictionary<string, DevelopmentUploadFile>(StringComparer.Ordinal)
        {
            ["manifest.json"] = new() { RelativePath = "manifest.json", Complete = true },
            ["__init__.py"] = new() { RelativePath = "__init__.py", Complete = true },
        },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
