using System.Text;
using HomeAssistantMCPSharp.Services;

namespace HomeAssistantMCPSharp.Tests;

public sealed class LocalConfigRootTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hamcp-tests-" + Guid.NewGuid().ToString("N"));

    public LocalConfigRootTransportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Writes_lists_reads_and_deletes_within_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new LocalConfigRootTransport(_root);
        var expected = Encoding.UTF8.GetBytes("export const value = 1;");

        await transport.WriteFileAtomicAsync("www/example/example.js", expected, ct);
        var files = await transport.ListFilesAsync("www/example", ct);

        var file = Assert.Single(files);
        Assert.Equal("www/example/example.js", file.RelativePath);
        Assert.Equal(expected, await transport.ReadFileAsync(file.RelativePath, ct));

        await transport.DeleteFileAsync(file.RelativePath, ct);
        Assert.Empty(await transport.ListFilesAsync("www/example", ct));
    }

    [Fact]
    public async Task Transport_rejects_paths_outside_root()
    {
        var transport = new LocalConfigRootTransport(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ReadFileAsync("../outside", TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
