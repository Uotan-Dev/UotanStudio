using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class McpAdbFileSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-adb-sync-" + Guid.NewGuid().ToString("N"));

    public McpAdbFileSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task AdbPullAsync_GatewayWritesStagingFile_MovesItToSandboxDestination()
    {
        var gateway = new RecordingGateway("device-payload");

        var result = await DeviceToolHandlers.AdbPullAsync(gateway, new PathSandbox(_root), "serial-1",
            "/sdcard/x.txt", "x.txt", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, gateway.PullCalls);
        Assert.Equal("/sdcard/x.txt", gateway.PullRemotePath);
        Assert.True(Path.IsPathRooted(gateway.PullLocalPath));
        Assert.EndsWith(".part", gateway.PullLocalPath, StringComparison.Ordinal);
        Assert.Equal("device-payload", File.ReadAllText(Path.Combine(_root, "x.txt")));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    [Fact]
    public async Task AdbPullAsync_GatewayFailsMidTransfer_DeletesStagingFileAndRethrows()
    {
        var gateway = new FailingGateway();

        await Assert.ThrowsAsync<IOException>(() => DeviceToolHandlers.AdbPullAsync(gateway, new PathSandbox(_root),
            "serial-1", "/sdcard/x.txt", "partial.txt", TestContext.Current.CancellationToken));

        Assert.Equal(1, gateway.PullCalls);
        Assert.False(File.Exists(Path.Combine(_root, "partial.txt")));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    [Fact]
    public async Task AdbPullAsync_PreCancelledToken_AbortsBeforeTouchingTheGateway()
    {
        var gateway = new RecordingGateway("never");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeviceToolHandlers.AdbPullAsync(gateway,
            new PathSandbox(_root), "serial-1", "/sdcard/x.txt", "cancelled.txt", cancellation.Token));

        Assert.Equal(0, gateway.PullCalls);
        Assert.False(File.Exists(Path.Combine(_root, "cancelled.txt")));
    }

    [Fact]
    public void ServerOptions_ClippedAutoSyncDisabled_EmitsOnlyTheNegativeArgument()
    {
        var enabled = new ScrcpyServerOptions { ClipboardAutoSync = true }.BuildArguments();
        var disabled = new ScrcpyServerOptions { ClipboardAutoSync = false }.BuildArguments();

        Assert.DoesNotContain(enabled, argument => argument.StartsWith("clipboard_autosync", StringComparison.Ordinal));
        Assert.Contains("clipboard_autosync=false", disabled);
    }

    private sealed class RecordingGateway(string payload) : IDeviceManagerGateway
    {
        public int PullCalls;
        public string? PullRemotePath;
        public string? PullLocalPath;

        public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => [];

        public IReadOnlyList<(string Serial, string State)> ListAdbTargets() => [("serial-1", "device")];

        public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PushAdbFile(string serial, string localPath, string remotePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PullAdbFile(string serial, string remotePath, string localPath,
            CancellationToken cancellationToken = default)
        {
            PullCalls++;
            PullRemotePath = remotePath;
            PullLocalPath = localPath;
            File.WriteAllText(localPath, payload);
        }

        public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (string ServiceName, string Password, string Payload) CreateQrPairingSession() =>
            throw new NotSupportedException();

        public Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
            int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailingGateway : IDeviceManagerGateway
    {
        public int PullCalls;

        public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => [];

        public IReadOnlyList<(string Serial, string State)> ListAdbTargets() => [("serial-1", "device")];

        public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PushAdbFile(string serial, string localPath, string remotePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PullAdbFile(string serial, string remotePath, string localPath,
            CancellationToken cancellationToken = default)
        {
            PullCalls++;
            File.WriteAllText(localPath, "half");
            throw new IOException("device closed the sync stream");
        }

        public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (string ServiceName, string Password, string Payload) CreateQrPairingSession() =>
            throw new NotSupportedException();

        public Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
            int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}