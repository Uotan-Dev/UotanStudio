using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceRemovalCorrelationTests
{
    private static readonly Guid FirstContainer = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid SecondContainer = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    [Fact]
    public async Task HandleDeviceRemoved_SameContainerRecycledPath_ReleasesLeaseAndReturnsReservation()
    {
        var endpoint = IdentifiedEndpoint("path-old", FirstContainer, serial: "SN-7");
        var opener = new Opener();
        var controller = new DeviceController([new Source(endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        DeviceEndpoint? removed = null;
        controller.DeviceRemoved += endpoint => removed = endpoint;

        controller.HandleDeviceRemoved(RemovedInfo("path-new", FirstContainer, serial: "SN-7"), metadataChanged: false);
        opener.Last!.Gate.SetResult();
        await lease.DisposeAsync();

        Assert.Same(endpoint, removed);
        Assert.True(opener.Last!.Disposed.Task.IsCompleted);
        var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        opener.Last!.Gate.SetResult();
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task HandleDeviceRemoved_DifferentContainerSamePath_KeepsLeaseAndReservation()
    {
        var endpoint = IdentifiedEndpoint("shared-path", FirstContainer, serial: "SN-7");
        var opener = new Opener();
        var controller = new DeviceController([new Source(endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        DeviceEndpoint? removed = null;
        controller.DeviceRemoved += endpoint => removed = endpoint;

        controller.HandleDeviceRemoved(RemovedInfo("shared-path", SecondContainer, serial: "SN-8"), metadataChanged: false);

        Assert.Null(removed);
        Assert.False(opener.Last!.Disposed.Task.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken));

        opener.Last!.Gate.SetResult();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task HandleDeviceRemoved_PortPathMatchWithoutContainers_ReleasesLease()
    {
        var endpoint = IdentifiedEndpoint("path-old", container: null, serial: "SN-7", port: @"2\1\3");
        var opener = new Opener();
        var controller = new DeviceController([new Source(endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        DeviceEndpoint? removedEndpoint = null;
        controller.DeviceRemoved += endpoint => removedEndpoint = endpoint;

        controller.HandleDeviceRemoved(
            RemovedInfo("path-new", container: null, serial: "SN-7", port: @"2\1\3"), metadataChanged: false);

        Assert.Same(endpoint, removedEndpoint);
        opener.Last!.Gate.SetResult();
        await lease.DisposeAsync();

        Assert.True(opener.Last!.Disposed.Task.IsCompleted);
        var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        opener.Last!.Gate.SetResult();
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task HandleDeviceRemoved_NoIdentityEvidenceSamePath_StillReleasesLease()
    {
        var endpoint = IdentifiedEndpoint("path-old", container: null, serial: null);
        var opener = new Opener();
        var controller = new DeviceController([new Source(endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);

        controller.HandleDeviceRemoved(RemovedInfo("path-old", container: null, serial: null), metadataChanged: false);
        opener.Last!.Gate.SetResult();
        await lease.DisposeAsync();

        Assert.True(opener.Last!.Disposed.Task.IsCompleted);
        var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        opener.Last!.Gate.SetResult();
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_LeaseOnSameContainerDifferentEndpoint_RejectsBeforeDiscovery()
    {
        var previous = ReconnectEndpoint("path-old", FirstContainer, address: "address-old");
        var sibling = ReconnectEndpoint("path-new", FirstContainer, address: "address-new");
        var source = new ReconnectSource([sibling]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var siblingLease = await controller.OpenAsync(snapshot, sibling.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.WaitForUsbReconnectAsync(
            previous, DeviceSessionKind.Mtk, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls);

        opener.Last!.Gate.SetResult();
        await siblingLease.DisposeAsync();
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_LeaseWithDifferentContainerSamePath_DoesNotBlock()
    {
        var previous = ReconnectEndpoint("shared-path", FirstContainer, address: "address-old");
        var otherDevice = ReconnectEndpoint("shared-path", SecondContainer, address: "address-other");
        var source = new ReconnectSource([otherDevice]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var otherLease = await controller.OpenAsync(snapshot, otherDevice.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(() => controller.WaitForUsbReconnectAsync(
            previous, DeviceSessionKind.Mtk, 100, TestContext.Current.CancellationToken));

        Assert.True(source.Calls > 1);
        opener.Last!.Gate.SetResult();
        await otherLease.DisposeAsync();
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_IndeterminateIdentitySamePathLease_RejectsBeforeDiscovery()
    {
        var previous = ReconnectEndpoint("shared-path", FirstContainer, address: "address-old");
        var lookalike = ReconnectEndpoint("shared-path", container: null, address: "address-new", serial: null, port: null);
        var source = new ReconnectSource([lookalike]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lookalikeLease = await controller.OpenAsync(snapshot, lookalike.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.WaitForUsbReconnectAsync(
            previous, DeviceSessionKind.Mtk, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls);

        opener.Last!.Gate.SetResult();
        await lookalikeLease.DisposeAsync();
    }

    private static DeviceEndpoint IdentifiedEndpoint(string path, Guid? container, string? serial, string? port = null)
    {
        var info = new UsbDeviceInfo
        {
            ApiName = "native",
            SourceApiKind = UsbApiKind.Native,
            DevicePath = path,
            ContainerId = container,
            PhysicalPortPath = port,
            SerialNumber = serial,
            VendorId = 0x18D1,
            ProductId = 0x4EE7,
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 0x01
        };
        return new DeviceEndpoint
        {
            Source = "usb",
            Transport = nameof(UsbApiKind.Native),
            Address = path,
            Route = "direct-usb",
            Protocol = "adb",
            UsbInfo = info,
            Usb = new(UsbApiKind.Native, path, info.VendorId, info.ProductId,
                info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol)
        };
    }

    private static UsbDeviceInfo RemovedInfo(string path, Guid? container, string? serial, string? port = null) => new()
    {
        ApiName = "native",
        SourceApiKind = UsbApiKind.Native,
        DevicePath = path,
        ContainerId = container,
        PhysicalPortPath = port,
        SerialNumber = serial,
        VendorId = 0x18D1,
        ProductId = 0x4EE7
    };

    private static DeviceEndpoint ReconnectEndpoint(
        string path, Guid? container, string address, string? serial = "SN-RC", string? port = null)
    {
        var info = new UsbDeviceInfo
        {
            ApiName = "test",
            SourceApiKind = UsbApiKind.Custom,
            DevicePath = path,
            PhysicalPortPath = port,
            ContainerId = container,
            SerialNumber = serial,
            VendorId = 0x0E8D,
            ProductId = 0x2001,
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 3
        };
        info.DeviceKey = UsbDeviceIdentity.BuildKey(info);
        return new DeviceEndpoint
        {
            Source = UsbDiscoverySource.SourceName,
            Transport = UsbApiKind.Custom.ToString(),
            Address = address,
            Serial = serial,
            Protocol = "mtk",
            Route = "direct-usb",
            UsbInfo = info,
            Usb = new(info.SourceApiKind, path, info.VendorId, info.ProductId,
                info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol)
        };
    }

    private sealed class Source(DeviceEndpoint endpoint) : IDeviceDiscoverySource
    {
        public string Name => endpoint.Source;

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceEndpoint>>([endpoint]);
    }

    private sealed class ReconnectSource(DeviceEndpoint[] endpoints) : IDeviceDiscoverySource
    {
        private int _calls;

        public string Name => UsbDiscoverySource.SourceName;

        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<IReadOnlyList<DeviceEndpoint>>(endpoints);
        }
    }

    private sealed class Opener : IDeviceTransportOpener
    {
        public GatedTransport? Last { get; private set; }

        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            Last = new GatedTransport();
            return Task.FromResult<IAsyncDisposable>(Last);
        }
    }

    private sealed class Factory(DeviceSessionKind kind) : IDeviceSessionFactory
    {
        public DeviceSessionKind Kind => kind;

        public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport,
            CancellationToken cancellationToken) => Task.FromResult<object>(new Session());
    }

    private sealed class Session : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class GatedTransport : IAsyncDisposable
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            await Gate.Task.ConfigureAwait(false);
            Disposed.TrySetResult();
        }
    }
}