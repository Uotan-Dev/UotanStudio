using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class McpDeviceManagerGatewayTests
{
    [Fact]
    public async Task AdbShellAsync_InjectedManager_ForwardsSelectedTargetAndCommand()
    {
        var manager = new RecordingManager();

        var result = await DeviceToolHandlers.AdbShellAsync(manager, "chosen", "echo ready",
            TestContext.Current.CancellationToken);

        Assert.Equal("chosen", manager.Serial);
        Assert.Equal("shell:echo ready", manager.Service);
        Assert.True(result.Success);
        Assert.Contains("ready", result.Data!.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAdbDevicesAsync_InjectedManager_UsesManagerEnumeration()
    {
        var manager = new RecordingManager();

        var result = await DeviceToolHandlers.ListAdbDevicesAsync(manager,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, manager.ListCalls);
        Assert.True(result.Success);
        var payload = result.Data!.Value.ToString();
        Assert.Contains("chosen", payload, StringComparison.Ordinal);
        Assert.Contains("offline", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FastbootVariablesAsync_SelectedTarget_ForwardsTargetToGateway()
    {
        var manager = new RecordingManager();

        await Assert.ThrowsAsync<NotSupportedException>(() => DeviceToolHandlers.FastbootVariablesAsync(
            manager, "device-key-1", TestContext.Current.CancellationToken));

        Assert.Equal(1, manager.FastbootCalls);
        Assert.Equal("device-key-1", manager.FastbootTarget);
    }

    [Fact]
    public async Task FastbootVariablesAsync_NoTarget_KeepsSingleDeviceRule()
    {
        var manager = new RecordingManager();

        await Assert.ThrowsAsync<NotSupportedException>(() => DeviceToolHandlers.FastbootVariablesAsync(
            manager, null, TestContext.Current.CancellationToken));

        Assert.Null(manager.FastbootTarget);
    }

    [Fact]
    public async Task ListDevicesAsync_DefaultRequest_KeepsClassifiedEndpointsOnly()
    {
        var manager = new RecordingManager();

        var result = await DeviceToolHandlers.ListDevicesAsync(manager, detailed: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("adb:1", result.Summary, StringComparison.Ordinal);
        Assert.Contains("unclassifiable", result.Summary, StringComparison.OrdinalIgnoreCase);
        var devices = result.Data!.Value.EnumerateArray().ToList();
        Assert.Single(devices);
        Assert.Equal("adb", devices[0].GetProperty("protocol").GetString());
        Assert.False(devices[0].TryGetProperty("devicePath", out _));
    }

    [Fact]
    public async Task ListDevicesAsync_DetailedRequest_ReturnsFullInventoryWithPath()
    {
        var manager = new RecordingManager();

        var result = await DeviceToolHandlers.ListDevicesAsync(manager, detailed: true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.DoesNotContain("unclassifiable", result.Summary, StringComparison.OrdinalIgnoreCase);
        var devices = result.Data!.Value.EnumerateArray().ToList();
        Assert.Equal(2, devices.Count);
        Assert.Equal(@"usb-hub-path", devices[1].GetProperty("devicePath").GetString());
    }

    [Fact]
    public async Task AdbShellAsync_CanceledRequest_StopsBeforeDeviceTransaction()
    {
        var manager = new RecordingManager();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeviceToolHandlers.AdbShellAsync(
            manager, "chosen", "echo ready", cts.Token));

        Assert.Null(manager.Service);
    }

    [Fact]
    public async Task QrPairingCodeAsync_InjectedGateway_ReturnsWifiPayloadAndCredentials()
    {
        var result = await DeviceToolHandlers.QrPairingCodeAsync(new RecordingManager(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Matches(@"^WIFI:T:ADB;S:ADB_WIFI_[0-9A-Za-z]{14}-[0-9A-Za-z]{6};P:[0-9A-Za-z]{21};;$",
            result.Summary);
        Assert.Contains("ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb", result.Data!.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdbPairQrAsync_InjectedGateway_ForwardsCredentialsAndReportsRoute()
    {
        var manager = new RecordingManager();

        var result = await DeviceToolHandlers.AdbPairQrAsync(manager, "ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb",
            "password1234567890ABC", 45, TestContext.Current.CancellationToken);

        Assert.Equal("ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb", manager.PairingServiceName);
        Assert.Equal("password1234567890ABC", manager.PairingPassword);
        Assert.Equal(45, manager.PairingTimeoutSeconds);
        Assert.True(result.Success);
        Assert.Contains("Direct", result.Summary, StringComparison.Ordinal);
        Assert.Contains("10.0.0.9:45321", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdbPairQrAsync_BlankServiceName_RejectsBeforePairing()
    {
        var manager = new RecordingManager();

        await Assert.ThrowsAsync<ArgumentException>(() => DeviceToolHandlers.AdbPairQrAsync(
            manager, " ", "password1234567890ABC", 30, TestContext.Current.CancellationToken));

        Assert.Null(manager.PairingServiceName);
    }

    private sealed class RecordingManager : IDeviceManagerGateway
    {
        public string? Serial { get; private set; }
        public string? Service { get; private set; }
        public string? FastbootTarget { get; private set; }
        public int ListCalls { get; private set; }
        public int FastbootCalls { get; private set; }

        public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() =>
        [
            new UsbDeviceInfo
            {
                ApiName = "native",
                SourceApiKind = UsbApiKind.Native,
                VendorId = 0x18D1,
                ProductId = 0x4EE7,
                SerialNumber = "adb-serial-1",
                DevicePath = @"usb-adb-path",
                DeviceKey = "adb-device-key",
                InterfaceClass = 0xFF,
                InterfaceSubClass = 0x42,
                InterfaceProtocol = 1,
                InterfaceMetadataObserved = true
            },
            new UsbDeviceInfo
            {
                ApiName = "native",
                SourceApiKind = UsbApiKind.Native,
                VendorId = 0x8087,
                ProductId = 0x0024,
                DevicePath = @"usb-hub-path",
                DeviceKey = "hub-device-key",
                InterfaceClass = 0x09,
                InterfaceMetadataObserved = true
            }
        ];

        public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
        {
            ListCalls++;
            return [("chosen", "offline")];
        }

        public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Serial = serial;
            Service = service;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("ready"));
            return operation(stream);
        }

        public void PushAdbFile(string serial, string localPath, string remotePath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public int PullCalls;
        public string? PullRemotePath;
        public string? PullLocalPath;

        public void PullAdbFile(string serial, string remotePath, string localPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PullCalls++;
            Serial = serial;
            PullRemotePath = remotePath;
            PullLocalPath = localPath;
        }

        public string? PairingServiceName { get; private set; }
        public string? PairingPassword { get; private set; }
        public int? PairingTimeoutSeconds { get; private set; }

        public (string ServiceName, string Password, string Payload) CreateQrPairingSession() =>
            ("ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb", "password1234567890ABC",
                "WIFI:T:ADB;S:ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb;P:password1234567890ABC;;");

        public Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
            int timeoutSeconds = 180, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PairingServiceName = serviceName;
            PairingPassword = password;
            PairingTimeoutSeconds = timeoutSeconds;
            return Task.FromResult(("Direct", "10.0.0.9:45321"));
        }

        public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
            CancellationToken cancellationToken = default)
        {
            FastbootCalls++;
            FastbootTarget = target;
            throw new NotSupportedException();
        }
    }
}