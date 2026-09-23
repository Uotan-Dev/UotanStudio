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
                InterfaceProtocol = 1
            }
        ];

        var endpoints = await new UsbDiscoverySource(kit, UsbApiKind.Native)
            .DiscoverAsync(TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("usb", endpoint.Source);
        Assert.Equal("Native", endpoint.Transport);
        Assert.Equal(proxy.Devices[0].DeviceKey, endpoint.Address);
        Assert.Equal("serial", endpoint.Serial);
        Assert.Equal("adb", endpoint.Protocol);
        Assert.Equal("direct-usb", endpoint.Route);
    }

    public sealed class UsbKitProxy : DispatchProxy
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
