using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceIdentityGenerationTests
{
    private static UsbDeviceInfo Node(Guid? container, string? port, string? serial, ushort pid = 0x0003) => new()
    {
        VendorId = 0x0E8D,
        ProductId = pid,
        SerialNumber = serial,
        ContainerId = container,
        PhysicalPortPath = port
    };

    [Fact]
    public void From_ContainerPresent_PrefersContainerKeyOverSerial()
    {
        var container = Guid.NewGuid();

        var identity = PhysicalDeviceIdentity.From(Node(container, @"2\1\3", "SN7"));

        Assert.NotNull(identity);
        Assert.Equal($"usb-container:{container:N}", identity!.Key);
        Assert.Equal(container, identity.ContainerId);
        Assert.Equal(@"2\1\3", identity.PhysicalPortPath);
    }

    [Fact]
    public void From_NoContainerValidSerial_UsesVidPidSerialKey()
    {
        var identity = PhysicalDeviceIdentity.From(Node(null, null, "SN7"));

        Assert.Equal("usb:0E8D:0003:SN7", identity!.Key);
        Assert.Null(identity.ContainerId);
    }

    [Fact]
    public void From_NoContainerInvalidSerial_YieldsNull()
    {
        Assert.Null(PhysicalDeviceIdentity.From(Node(null, @"2\1\3", "UNKNOWN")));
        Assert.Null(PhysicalDeviceIdentity.From(Node(null, null, null)));
    }

    [Fact]
    public void CorrelatesWith_ContainersDiffer_ReturnsDifferentEvenWhenPortMatches()
    {
        var first = PhysicalDeviceIdentity.From(Node(Guid.NewGuid(), @"2\1\3", null))!;
        var second = PhysicalDeviceIdentity.From(Node(Guid.NewGuid(), @"2\1\3", null))!;

        Assert.Equal(DeviceCorrelationResult.Different, first.CorrelatesWith(second));
    }

    [Fact]
    public void CorrelatesWith_OneSideLacksContainer_FallsBackToPortVerdict()
    {
        var first = PhysicalDeviceIdentity.From(Node(Guid.NewGuid(), @"2\1\3", null))!;
        var second = PhysicalDeviceIdentity.From(Node(null, @"2\1\3", "SN7"))!;

        Assert.Equal(DeviceCorrelationResult.Same, first.CorrelatesWith(second));
    }

    [Fact]
    public void CorrelatesWith_OnlySerialEvidence_ReturnsIndeterminate()
    {
        var first = PhysicalDeviceIdentity.From(Node(null, null, "SN7"))!;
        var second = PhysicalDeviceIdentity.From(Node(null, null, "SN7"))!;

        Assert.Equal(DeviceCorrelationResult.Indeterminate, first.CorrelatesWith(second));
        Assert.Equal(DeviceCorrelationResult.Indeterminate, first.CorrelatesWith(null));
    }

    [Fact]
    public async Task RefreshAsync_SameContainerProtocolChanged_PublishesContainerEvidenceTransition()
    {
        var container = Guid.NewGuid();
        var source = new SwappingSource(
            [EndpointFor(Node(container, @"2\1\3", null), "edl")],
            [EndpointFor(Node(container, @"2\4\1", null, pid: 0x2001), "mtk")]);
        var controller = new DeviceController([source], new Opener());
        var transitions = new List<ModeTransition>();
        controller.ModeTransitionsObserved += transitions.Add;

        await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await controller.RefreshAsync(TestContext.Current.CancellationToken);

        var transition = Assert.Single(transitions);
        Assert.Equal("edl", transition.SourceMode);
        Assert.Equal("mtk", transition.TargetMode);
        Assert.Equal(ModeTransitionEvidence.Container, transition.Correlation);
        Assert.Equal(1UL, transition.Generation.Value);
        Assert.True(controller.TryPeekTransportGeneration(transition.Identity, out var current));
        Assert.Equal(transition.Generation, current);
    }

    [Fact]
    public async Task RefreshAsync_SerialOnlyIdentityChange_PublishesNothing()
    {
        var source = new SwappingSource(
            [EndpointFor(Node(null, null, "SN-A"), "edl")],
            [EndpointFor(Node(null, null, "SN-A", pid: 0x2001), "mtk")]);
        var controller = new DeviceController([source], new Opener());
        var transitions = new List<ModeTransition>();
        controller.ModeTransitionsObserved += transitions.Add;

        await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await controller.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(transitions);
    }

    [Fact]
    public async Task OpenAsync_IdentifiedEndpoint_StampsMonotonicGenerationPerReopen()
    {
        var endpoint = EndpointFor(Node(Guid.NewGuid(), @"2\1\3", null), "edl");
        var controller = new DeviceController(
            [new FixedSource("usb", endpoint)], new Opener());
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);

        await using (var first = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken))
        {
            Assert.Equal(1UL, first.Generation!.Value.Value);
        }

        await using (var second = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken))
        {
            Assert.Equal(2UL, second.Generation!.Value.Value);
        }

        var serverEndpoint = new DeviceEndpoint { Source = "adb", Transport = "server", Address = "serial-a" };
        var serverController = new DeviceController(
            [new FixedSource("adb", serverEndpoint)], new Opener());
        var serverSnapshot = await serverController.RefreshAsync(TestContext.Current.CancellationToken);
        await using var serverLease = await serverController.OpenAsync(
            serverSnapshot, serverEndpoint.Id, TestContext.Current.CancellationToken);

        Assert.Null(serverLease.Generation);
    }

    private static DeviceEndpoint EndpointFor(UsbDeviceInfo info, string protocol) => new()
    {
        Source = "usb",
        Transport = info.SourceApiKind.ToString(),
        Address = $"key-{protocol}-{info.SerialNumber ?? info.ContainerId?.ToString("N")}",
        Route = "direct-usb",
        Protocol = protocol,
        UsbInfo = info,
        Usb = new(UsbApiKind.Native,
            info.DevicePath.Length > 0 ? info.DevicePath : $@"\\?\usb#vid_0e8d&pid_{info.ProductId:x4}",
            info.VendorId, info.ProductId, 0xFF, 0x02, 0x00)
    };

    private sealed class FixedSource(string name, DeviceEndpoint endpoint) : IDeviceDiscoverySource
    {
        public string Name => name;
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceEndpoint>>([endpoint]);
    }

    private sealed class SwappingSource(params DeviceEndpoint[][] snapshots) : IDeviceDiscoverySource
    {
        private int _call;

        public string Name => "usb";

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(_call, snapshots.Length - 1);
            Interlocked.Increment(ref _call);
            return Task.FromResult<IReadOnlyList<DeviceEndpoint>>(snapshots[index]);
        }
    }

    private sealed class Opener : IDeviceTransportOpener
    {
        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new Transport());
    }

    private sealed class Transport : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}