using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed partial class DeviceReconnectionTests
{
    [Theory]
    [InlineData(DeviceSessionKind.Mtk, "mtk")]
    [InlineData(DeviceSessionKind.Edl, "edl")]
    [InlineData(DeviceSessionKind.Fastboot, "fastboot")]
    public async Task WaitForUsbReconnectAsync_ExplicitPortWithoutContainer_OpensOnlySamePort(
        DeviceSessionKind kind, string protocol)
    {
        var previous = PortEndpoint("old", "libusb-port:1:2.3");
        var peer = PortEndpoint("peer", "libusb-port:1:2.4", protocol);
        var target = PortEndpoint("new", "libusb-port:1:2.3", protocol);
        var source = new Source([previous], [peer], [peer, target]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(kind)]);

        await using var lease = await controller.WaitForUsbReconnectAsync(previous, kind,
            UsbReconnectIdentityPolicy.PhysicalPort, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(target.Id, lease.Endpoint.Id);
        Assert.Equal(new[] { target.Id }, opener.Opened);
        Assert.Equal(3, source.Calls);
        Assert.All(opener.Devices, device => Assert.Equal(0, device.IoCalls));
        Assert.Equal("libusb-port:1:2.3", lease.Endpoint.UsbInfo!.PhysicalPortPath);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_DefaultPolicy_DoesNotFallBackToObservedPort()
    {
        var previous = PortEndpoint("old", "libusb-port:1:2.3");
        var source = new Source([PortEndpoint("new", "libusb-port:1:2.3")]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<NotSupportedException>(() => controller.WaitForUsbReconnectAsync(previous,
            DeviceSessionKind.Mtk, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Calls);
        Assert.Empty(opener.Opened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("port:1 2")]
    [InlineData("port:\n1")]
    [InlineData("port:\0")]
    public async Task WaitForUsbReconnectAsync_PortEvidenceAbsentOrInvalid_RejectsBeforeDiscovery(string? port)
    {
        var previous = PortEndpoint("old", port);
        var source = new Source([]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<NotSupportedException>(() => controller.WaitForUsbReconnectAsync(previous,
            DeviceSessionKind.Mtk, UsbReconnectIdentityPolicy.PhysicalPort,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Calls);
        Assert.Empty(opener.Opened);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-port")]
    [InlineData("other-bus")]
    [InlineData("case")]
    [InlineData("unchanged")]
    [InlineData("container-conflict")]
    public async Task WaitForUsbReconnectAsync_NoMatchingPortTransition_DoesNotOpen(string failure)
    {
        var previous = PortEndpoint("old", "libusb-port:1:2.3", container: Container);
        var target = failure switch
        {
            "missing" => PortEndpoint("new", null),
            "other-port" => PortEndpoint("new", "libusb-port:1:2.4"),
            "other-bus" => PortEndpoint("new", "libusb-port:2:2.3"),
            "case" => PortEndpoint("new", "LIBUSB-PORT:1:2.3"),
            "container-conflict" => PortEndpoint("new", "libusb-port:1:2.3", container: Guid.NewGuid()),
            _ => previous
        };
        var opener = new Opener();
        var controller = new DeviceController([new Source([target])], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<TimeoutException>(() => controller.WaitForUsbReconnectAsync(previous,
            DeviceSessionKind.Mtk, UsbReconnectIdentityPolicy.PhysicalPort, 100, TestContext.Current.CancellationToken));

        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_SamePortMultipleInterfaces_RejectsAmbiguity()
    {
        var previous = PortEndpoint("old", "libusb-port:1:2.3");
        var source = new Source([PortEndpoint("one", "libusb-port:1:2.3"), PortEndpoint("two", "libusb-port:1:2.3")]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.WaitForUsbReconnectAsync(previous,
            DeviceSessionKind.Mtk, UsbReconnectIdentityPolicy.PhysicalPort,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(opener.Opened);
    }

    [Theory]
    [InlineData("port")]
    [InlineData("missing-port")]
    [InlineData("new-container-conflict")]
    public async Task WaitForUsbReconnectAsync_OpenedPortEvidenceChanges_ReleasesWithoutCreatingSession(string failure)
    {
        var previous = PortEndpoint("old", "libusb-port:1:2.3");
        var target = PortEndpoint("new", "libusb-port:1:2.3", container: Container);
        var opener = new Opener
        {
            ChangeOpenedInfo = info =>
            {
                if (failure == "new-container-conflict")
                {
                    info.ContainerId = Guid.NewGuid();
                }
                else
                {
                    info.PhysicalPortPath = failure == "port" ? "libusb-port:1:2.4" : null;
                }
            }
        };
        var factory = new Factory(DeviceSessionKind.Mtk);
        var controller = new DeviceController([new Source([target])], opener, [factory]);

        await Assert.ThrowsAsync<IOException>(() => controller.WaitForUsbReconnectAsync(previous,
            DeviceSessionKind.Mtk, UsbReconnectIdentityPolicy.PhysicalPort,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, factory.Calls);
        Assert.Equal(1, Assert.Single(opener.Devices).DisposeCount);
        opener.ChangeOpenedInfo = null;
        await using var lease = await controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            UsbReconnectIdentityPolicy.PhysicalPort, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, lease.Endpoint.Id);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_UnknownIdentityPolicy_RejectsBeforeDiscovery()
    {
        var source = new Source([]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.WaitForUsbReconnectAsync(
            PortEndpoint("old", "libusb-port:1:2.3"), DeviceSessionKind.Mtk, (UsbReconnectIdentityPolicy)2,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Calls);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public void DeviceEndpoint_PortMetadata_IsCopiedWithoutChangingEndpointIdentity()
    {
        var original = Endpoint("same", "mtk");
        var info = original.UsbInfo!;
        info.PhysicalPortPath = "libusb-port:1:2.3";
        var endpoint = original with { UsbInfo = info };
        info.PhysicalPortPath = "libusb-port:1:2.4";

        Assert.Equal("libusb-port:1:2.3", endpoint.UsbInfo!.PhysicalPortPath);
        Assert.Equal(original.Id, endpoint.Id);
        endpoint.UsbInfo.PhysicalPortPath = "libusb-port:9:9";
        Assert.Equal("libusb-port:1:2.3", endpoint.UsbInfo.PhysicalPortPath);
    }

    private static DeviceEndpoint PortEndpoint(string path, string? port, string protocol = "mtk", Guid? container = null)
    {
        var endpoint = Endpoint(path, protocol);
        var info = endpoint.UsbInfo!;
        info.ContainerId = container;
        info.PhysicalPortPath = port;
        return endpoint with { UsbInfo = info };
    }
}