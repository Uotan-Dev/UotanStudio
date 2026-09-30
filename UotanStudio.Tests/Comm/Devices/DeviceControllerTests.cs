using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceControllerTests
{
    private static readonly DeviceEndpoint First = new() { Source = "adb", Transport = "server", Address = "serial-a" };
    private static readonly DeviceEndpoint Second = new() { Source = "adb", Transport = "server", Address = "serial-b" };

    [Fact]
    public async Task OpenAsync_ExactEndpoint_LeasesUntilDisposed()
    {
        var opener = new Opener();
        var controller = new DeviceController([new Source("adb", _ => Task.FromResult<IReadOnlyList<DeviceEndpoint>>([First, Second]))], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);

        await using (var lease = await controller.OpenAsync(snapshot, Second.Id, TestContext.Current.CancellationToken))
        {
            Assert.Equal(Second, lease.Endpoint);
            Assert.Same(opener.LastTransport, lease.Transport);
            Assert.Equal(Second, opener.LastEndpoint);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.OpenAsync(snapshot, Second.Id, TestContext.Current.CancellationToken));
        }

        Assert.True(opener.LastTransport!.Disposed);
        await using var reopened = await controller.OpenAsync(snapshot, Second.Id, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => controller.OpenAsync(snapshot, new DeviceEndpointId("adb", "usb", "serial-b"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_OlderCompletion_DoesNotReplaceNewerSnapshot()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<DeviceEndpoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new Source("adb", _ => Interlocked.Increment(ref calls) == 1
            ? pending.Task : Task.FromResult<IReadOnlyList<DeviceEndpoint>>([Second]));
        var controller = new DeviceController([source], new Opener());
        var older = controller.RefreshAsync(TestContext.Current.CancellationToken);
        var queued = controller.RefreshAsync(TestContext.Current.CancellationToken);
        pending.SetResult([First]);

        var committed = await older;
        Assert.Same(committed, await queued);
        Assert.Equal([First], controller.Snapshot.Endpoints);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OpenAsync_RefreshDuringOpen_DisposesTransportAndRejectsStaleLease()
    {
        var pending = new TaskCompletionSource<IAsyncDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opener = new Opener { Open = (_, _) => pending.Task };
        var controller = new DeviceController([new Source("adb", _ => Task.FromResult<IReadOnlyList<DeviceEndpoint>>([First]))], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var opening = controller.OpenAsync(snapshot, First.Id, TestContext.Current.CancellationToken);
        await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var transport = new Transport();
        pending.SetResult(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.OpenAsync(snapshot, First.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_CancelledDuringOpen_DisposesReturnedTransport()
    {
        var pending = new TaskCompletionSource<IAsyncDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opener = new Opener { Open = (_, _) => pending.Task };
        var controller = new DeviceController([new Source("adb", _ => Task.FromResult<IReadOnlyList<DeviceEndpoint>>([First]))], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var opening = controller.OpenAsync(snapshot, First.Id, cancellation.Token);
        cancellation.Cancel();
        var transport = new Transport();
        pending.SetResult(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.True(transport.Disposed);
        await using var retry = await controller.OpenAsync(snapshot, First.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RefreshAsync_InvalidSourceIdentity_DoesNotPublish()
    {
        var controller = new DeviceController(
            [new Source("usb", _ => Task.FromResult<IReadOnlyList<DeviceEndpoint>>([First]))], new Opener());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(controller.Snapshot.Endpoints);
    }

    [Fact]
    public async Task HandleDeviceRemoved_MatchingLease_IsDisposedAndReservationReleased()
    {
        var endpoint = new DeviceEndpoint
        {
            Source = "usb",
            Transport = "Native",
            Address = "key-a",
            Route = "direct-usb",
            Usb = new(UsbApiKind.Native, @"\\?\usb#vid_18d1&pid_4ee7#test", 0x18D1, 0x4EE7, 0xFF, 0x42, 0x01)
        };
        var controller = new DeviceController(
            [new Source("usb", _ => Task.FromResult<IReadOnlyList<DeviceEndpoint>>([endpoint]))], new Opener());
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        DeviceEndpoint? removed = null;
        controller.DeviceRemoved += endpoint => removed = endpoint;

        await using (var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken))
        {
            Assert.True(lease.Transport is DeviceControllerTests.Transport);
            controller.HandleDeviceRemoved(new UsbDeviceInfo
            {
                ApiName = "native",
                SourceApiKind = UsbApiKind.Native,
                DevicePath = @"\\?\usb#vid_18d1&pid_4ee7#test"
            }, metadataChanged: false);
        }

        Assert.Same(endpoint, removed);
        await using var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleDeviceRemoved_MetadataChanged_RaisesChangedAndKeepsCache()
    {
        var endpoint = new DeviceEndpoint
        {
            Source = "usb",
            Transport = "Native",
            Address = "key-b",
            Route = "direct-usb",
            Usb = new(UsbApiKind.Native, @"\\?\usb#vid_18d1&pid_4ee2#test", 0x18D1, 0x4EE2, 0xFF, 0x42, 0x01)
        };
        var controller = new DeviceController(
            [new Source("usb", _ => Task.FromResult<IReadOnlyList<DeviceEndpoint>>([endpoint]))], new Opener());
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        DeviceEndpoint? changed = null;
        DeviceEndpoint? removed = null;
        controller.DeviceChanged += endpoint => changed = endpoint;
        controller.DeviceRemoved += endpoint => removed = endpoint;
        controller.Properties.Store(endpoint.Address, [new KeyValuePair<string, string>("model", "pixel")]);

        await using (var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken))
        {
            controller.HandleDeviceRemoved(new UsbDeviceInfo
            {
                ApiName = "native",
                SourceApiKind = UsbApiKind.Native,
                DevicePath = @"\\?\usb#vid_18d1&pid_4ee2#test"
            }, metadataChanged: true);
        }

        Assert.Same(endpoint, changed);
        Assert.Null(removed);
        Assert.Equal("pixel", controller.Properties.Get(endpoint.Address)!["model"]);
    }

    private sealed class Source(string name, Func<CancellationToken, Task<IReadOnlyList<DeviceEndpoint>>> discover)
        : IDeviceDiscoverySource
    {
        public string Name => name;
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken) => discover(cancellationToken);
    }

    private sealed class Opener : IDeviceTransportOpener
    {
        public Func<DeviceEndpoint, CancellationToken, Task<IAsyncDisposable>>? Open { get; init; }
        public DeviceEndpoint? LastEndpoint { get; private set; }
        public Transport? LastTransport { get; private set; }

        public async Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            LastEndpoint = endpoint;
            if (Open is not null)
            {
                return await Open(endpoint, cancellationToken);
            }

            LastTransport = new Transport();
            return LastTransport;
        }
    }

    private sealed class Transport : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}