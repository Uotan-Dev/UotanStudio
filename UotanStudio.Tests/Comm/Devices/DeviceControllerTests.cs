using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
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
        var oldRefresh = controller.RefreshAsync(TestContext.Current.CancellationToken);
        var newest = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        pending.SetResult([First]);

        Assert.Same(newest, await oldRefresh);
        Assert.Equal([Second], controller.Snapshot.Endpoints);
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
