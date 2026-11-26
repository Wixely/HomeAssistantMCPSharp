using HomeAssistantMCPSharp.Services;

namespace HomeAssistantMCPSharp.Tests;

public sealed class DevelopmentPathPolicyTests
{
    [Theory]
    [InlineData("../secrets.yaml")]
    [InlineData("folder/../../secrets.yaml")]
    [InlineData("C:/secrets.py")]
    [InlineData("folder/file.exe")]
    public void Integration_paths_reject_unsafe_values(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            DevelopmentPathPolicy.NormalizeRelativeFile("custom_integration", path));
    }

    [Fact]
    public void Package_names_follow_home_assistant_domain_rules()
    {
        Assert.Equal("sample_bridge", DevelopmentPathPolicy.ValidatePackageName("custom_integration", "sample_bridge"));
        Assert.Throws<ArgumentException>(() => DevelopmentPathPolicy.ValidatePackageName("custom_integration", "sample-bridge"));
    }
}
