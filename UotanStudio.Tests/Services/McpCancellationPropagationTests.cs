using System.IO.Compression;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class McpCancellationPropagationTests : IDisposable
{
    private readonly string _root;

    public McpCancellationPropagationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-mcp-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ExtractHandler_PreCanceledToken_ThrowsBeforeWritingEntries()
    {
        var sandbox = new PathSandbox(_root);
        var container = CreateZipContainer("container.zip", entries: 64);
        var output = Path.Combine(_root, "out");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FirmwareToolHandlers.ExtractAsync(
            sandbox,
            container,
            ["member-0.bin"],
            output,
            fromPayloadOnly: false,
            maximumOutputBytes: 1L * 1024 * 1024 * 1024,
            progress: null,
            cancellationToken: cts.Token));

        Assert.False(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any());
    }

    [Fact]
    public async Task ExtractTool_PreCanceledToken_ReportsCancellationAsProtocolError()
    {
        var sandbox = new PathSandbox(_root);
        var container = CreateZipContainer("reported.zip", entries: 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ToolGuard.RunAsync(
            token => FirmwareToolHandlers.ExtractAsync(
                sandbox,
                container,
                ["member-0.bin"],
                Path.Combine(_root, "out-reported"),
                fromPayloadOnly: false,
                maximumOutputBytes: 1L * 1024 * 1024 * 1024,
                progress: null,
                cancellationToken: token),
            cts.Token);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("canceled", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractTool_NoProgressSinkAndLiveToken_CompletesWithoutProgressReports()
    {
        var sandbox = new PathSandbox(_root);
        var container = CreateZipContainer("uncanceled.zip", entries: 4);
        var progress = new CountingProgress();

        var result = await ToolGuard.RunAsync(
            token => FirmwareToolHandlers.ExtractAsync(
                sandbox,
                container,
                ["member-0.bin", "member-1.bin"],
                Path.Combine(_root, "out-live"),
                fromPayloadOnly: false,
                maximumOutputBytes: 1L * 1024 * 1024 * 1024,
                progress: progress,
                cancellationToken: token),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.True(File.Exists(Path.Combine(_root, "out-live", "member-0.bin")));
        await progress.WaitForFirstReportAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(progress.Count >= 1, "a supplied progress sink must receive byte reports");
    }

    [Fact]
    public async Task UnpackTool_PreCanceledToken_IsReportedNotGreen()
    {
        var sandbox = new PathSandbox(_root);
        var container = CreateZipContainer("unpack.zip", entries: 32);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ToolGuard.RunAsync(
            token => FirmwareToolHandlers.UnpackAsync(
                sandbox,
                container,
                Path.Combine(_root, "out-unpack"),
                maximumOutputBytes: 1L * 1024 * 1024 * 1024,
                cancellationToken: token),
            cts.Token);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("canceled", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateZipContainer(string name, int entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);
        for (var index = 0; index < entries; index++)
        {
            var entry = archive.CreateEntry($"member-{index}.bin");
            using var stream = entry.Open();
            stream.Write(payload);
        }

        return path;
    }

    private sealed class CountingProgress : IProgress<ProgressNotificationValue>
    {
        private readonly TaskCompletionSource _firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task WaitForFirstReportAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _firstReport.Task.WaitAsync(timeout, cancellationToken);

        public void Report(ProgressNotificationValue value)
        {
            Interlocked.Increment(ref _count);
            _firstReport.TrySetResult();
        }
    }
}