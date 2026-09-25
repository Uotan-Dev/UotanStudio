using System.Reflection;
using UotanStudio.Comm;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class UsbDiscoverySourceTests
{
    [Fact]
    public async Task DiscoverAsync_ProjectsExactBackendKeyAndProtocol()
    {
        var kit = DispatchProxy.Create<ICommKit, UsbKitProxy>();
        var proxy = (UsbKitProxy)(object)kit;
        proxy.Devices =
        [
            new UsbDeviceInfo
            {
                ApiName = "native",
                SourceApiKind = UsbApiKind.Native,
                DevicePath = "physical-path",
                DeviceKey = "native|Native|18D1|4EE7|FF|42|01|serial|physical-path",
                SerialNumber = "serial",
                InterfaceClass = 0xff,
                InterfaceSubClass = 0x42,
                InterfaceProtocol = 1,
                InterfaceMetadataObserved = true
            }
        ];

        var endpoints = await new UsbDiscoverySource(kit, UsbApiKind.Native, serverProbeOverride: _ => false)
            .DiscoverAsync(TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("usb", endpoint.Source);
        Assert.Equal("Native", endpoint.Transport);
        Assert.Equal(proxy.Devices[0].DeviceKey, endpoint.Address);
        Assert.Equal("serial", endpoint.Serial);
        Assert.Equal("adb", endpoint.Protocol);
        Assert.Equal("direct-usb", endpoint.Route);
        Assert.Equal(proxy.Devices[0].DeviceKey, endpoint.UsbInfo!.DeviceKey);
        endpoint.UsbInfo.DeviceKey = "mutated";
        Assert.Equal(proxy.Devices[0].DeviceKey, endpoint.UsbInfo.DeviceKey);
    }

    [Theory]
    [InlineData(0x05C6, 0x9008, 0, 0, 0, false, "edl")]
    [InlineData(0x2207, 0x330C, 0, 0, 0, false, "rkdl")]
    [InlineData(0x18D1, 0x4EE7, 0xFF, 0x42, 3, true, "fastboot")]
    [InlineData(0x18D1, 0x4EE7, 0xFF, 0x50, 1, true, "hdc")]
    [InlineData(0x18D1, 0x4EE7, 0xFF, 0x42, 3, false, null)]
    [InlineData(0x18D1, 0x4EE7, 0, 0, 0, false, null)]
    public async Task DiscoverAsync_ClassifiesOnlyObservedOrKnownUsb(
        int vid, int pid, byte cls, byte sub, byte protocol, bool observed, string? expected)
    {
        var kit = DispatchProxy.Create<ICommKit, UsbKitProxy>();
        ((UsbKitProxy)(object)kit).Devices =
        [
            new UsbDeviceInfo
            {
                SourceApiKind = UsbApiKind.Native,
                DevicePath = "physical-path",
                DeviceKey = $"native|Native|{vid:X4}|{pid:X4}|{cls:X2}|{sub:X2}|{protocol:X2}||physical-path",
                VendorId = (ushort)vid,
                ProductId = (ushort)pid,
                InterfaceClass = cls,
                InterfaceSubClass = sub,
                InterfaceProtocol = protocol,
                InterfaceMetadataObserved = observed
            }
        ];
        var endpoint = Assert.Single(await new UsbDiscoverySource(kit, UsbApiKind.Native, serverProbeOverride: _ => false)
            .DiscoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, endpoint.Protocol);
    }

    public class UsbKitProxy : DispatchProxy
    {
        public IReadOnlyList<UsbDeviceInfo> Devices { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ICommKit.EnumerateUsbDevicesAsync))
            {
                return Task.FromResult(Devices);
            }

            throw new InvalidOperationException("Unexpected CommKit call.");
        }
    }
}