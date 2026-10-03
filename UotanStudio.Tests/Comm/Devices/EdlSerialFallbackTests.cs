using System.Reflection;
using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class EdlSerialFallbackTests
{
    private const string UsbDeviceInterfaceGuid = "a5dcbf10-4316-11d4-9877-00c04f2eea20";

    [Theory]
    [InlineData(@"\\?\usb#vid_05c6&pid_9008#20000030#{a5dcbf10-4316-11d4-9877-00c04f2eea20}",
        @"usb\vid_05c6&pid_9008\20000030")]
    [InlineData(@"\\?\usb#vid_05c6&pid_9008&mi_00#20000030#{a5dcbf10-4316-11d4-9877-00c04f2eea20}",
        @"usb\vid_05c6&pid_9008&mi_00\20000030")]
    [InlineData(@"usb#vid_05c6&pid_9008#20000030#{a5dcbf10-4316-11d4-9877-00c04f2eea20}",
        @"usb\vid_05c6&pid_9008\20000030")]
    public void TryDeriveInstanceId_DeviceInterfacePath_ProducesPnpInstanceId(string devicePath, string expected)
    {
        Assert.Equal(expected, EdlSerialPortFallback.TryDeriveInstanceId(devicePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"   ")]
    [InlineData(@"\\?\pci#ven_104c&dev_8025#3&13c0b0c5&0#48#{a5dcbf10-4316-11d4-9877-00c04f2eea20}")]
    public void TryDeriveInstanceId_NonUsbOrBlankPath_ReturnsNull(string? devicePath)
    {
        Assert.Null(EdlSerialPortFallback.TryDeriveInstanceId(devicePath));
    }

    [Fact]
    public void TryDeriveInstanceId_OverlongUsbPath_ReturnsNull()
    {
        var serial = new string('0', 250);
        var devicePath = $@"\\?\usb#vid_05c6&pid_9008#{serial}#{{{UsbDeviceInterfaceGuid}}}";
        Assert.Null(EdlSerialPortFallback.TryDeriveInstanceId(devicePath));
    }

    [Fact]
    public async Task OpenAsync_UnopenableEdlEndpointWithoutResolvableSerial_KeepsExactEndpointFailure()
    {
        var opener = new CommDeviceTransportOpener(NullSessionKit());
        var endpoint = EdlEndpoint("fallback-missing", "edl");
        var error = await Assert.ThrowsAsync<IOException>(() =>
            opener.OpenAsync(endpoint, TestContext.Current.CancellationToken));
        Assert.Equal("Exact USB endpoint is unavailable.", error.Message);
    }

    [Fact]
    public async Task OpenAsync_NonEdlEndpointWithoutSession_DoesNotResolveSerialFallback()
    {
        var opener = new CommDeviceTransportOpener(NullSessionKit());
        var endpoint = EdlEndpoint("fallback-missing", "mtk");
        var error = await Assert.ThrowsAsync<IOException>(() =>
            opener.OpenAsync(endpoint, TestContext.Current.CancellationToken));
        Assert.Equal("Exact USB endpoint is unavailable.", error.Message);
    }

    [Fact]
    public async Task CreateAsync_SerialLeasedTransport_BuildsEdlSessionWithoutUsbRequirements()
    {
        var factory = new EdlDeviceSessionFactory();
        await using var transport = new OwnedDeviceTransport<SerialTransport>(new SerialTransport("COM0"));
        var created = await factory.CreateAsync(Endpoint("edl"), transport, TestContext.Current.CancellationToken);
        try
        {
            var session = Assert.IsType<EdlSession>(created);
            Assert.Equal("edl", session.ProtocolName);
        }
        finally
        {
            (created as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task CreateAsync_WrongProtocolOnEndpoint_IsRejectedBeforeSessionConstruction()
    {
        var factory = new EdlDeviceSessionFactory();
        await using var transport = new OwnedDeviceTransport<SerialTransport>(new SerialTransport("COM0"));
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateAsync(
            Endpoint("adb"), transport, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAsync_UnrelatedOwnedTransport_FailsTheExactTransportRequirement()
    {
        var factory = new EdlDeviceSessionFactory();
        await using var transport = new OwnedDeviceTransport<TrackedDisposable>(new TrackedDisposable());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(
            Endpoint("edl"), transport, TestContext.Current.CancellationToken));
        Assert.Contains("does not carry the exact transport", error.Message, StringComparison.Ordinal);
    }

    private static DeviceEndpoint Endpoint(string protocol) => new()
    {
        Source = "usb-test",
        Transport = "usb",
        Address = "edl-test",
        Serial = "edl-test",
        Route = "usb",
        Protocol = protocol
    };

    private static DeviceEndpoint EdlEndpoint(string serial, string protocol)
    {
        var path = $@"\\?\usb#vid_05c6&pid_9008#{serial}#{{{UsbDeviceInterfaceGuid}}}";
        return new DeviceEndpoint
        {
            Source = "usb",
            Transport = UsbApiKind.Native.ToString(),
            Route = "direct-usb",
            Address = $"native|Native|05C6|9008|FF|FF|10|{serial}|{path}",
            Serial = serial,
            Protocol = protocol,
            Usb = new UsbEndpointDetails(UsbApiKind.Native, path, 0x05C6, 0x9008, 0xFF, 0xFF, 0x10)
        };
    }

    private static ICommKit NullSessionKit()
    {
        var kit = DispatchProxy.Create<ICommKit, NullSessionKitProxy>();
        return kit;
    }

    private class NullSessionKitProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ICommKit.OpenUsbDeviceSessionByKey))
            {
                return null;
            }

            throw new InvalidOperationException("Unexpected CommKit call.");
        }
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}