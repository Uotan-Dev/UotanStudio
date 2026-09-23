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

        var result = await DeviceToolHandlers.AdbShellAsync(manager, "chosen", "echo ready");

        Assert.Equal("chosen", manager.Serial);
        Assert.Equal("shell:echo ready", manager.Service);
        Assert.Contains("ready", result);
    }

    [Fact]
    public async Task ListAdbDevicesAsync_InjectedManager_UsesManagerEnumeration()
    {
        var manager = new RecordingManager();

        var result = await DeviceToolHandlers.ListAdbDevicesAsync(manager);

        Assert.Equal(1, manager.ListCalls);
        Assert.Contains("chosen", result);
        Assert.Contains("offline", result);
    }

    [Fact]
    public async Task FastbootVariablesAsync_InjectedManager_RequestsManagedTransaction()
    {
        var manager = new RecordingManager();

        await Assert.ThrowsAsync<NotSupportedException>(() => DeviceToolHandlers.FastbootVariablesAsync(manager));

        Assert.Equal(1, manager.FastbootCalls);
    }

    private sealed class RecordingManager : IDeviceManagerGateway
    {
        public string? Serial { get; private set; }
        public string? Service { get; private set; }
        public int ListCalls { get; private set; }
        public int FastbootCalls { get; private set; }

        public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => [];

        public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
        {
            ListCalls++;
            return [("chosen", "offline")];
        }

        public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation)
        {
            Serial = serial;
            Service = service;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("ready"));
            return operation(stream);
        }

        public void PushAdbFile(string serial, string localPath, string remotePath)
            => throw new NotSupportedException();

        public TResult RunFastboot<TResult>(Func<FastbootSession, TResult> operation)
        {
            FastbootCalls++;
            throw new NotSupportedException();
        }
    }
}