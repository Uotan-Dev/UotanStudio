using System.Reflection;
using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceLeaseLifecycleTests
{
    [Fact]
    public async Task HandleDeviceRemoved_SlowTransportTeardown_KeepsEndpointReservedUntilReleaseCompletes()
    {
        var endpoint = UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#slow");
        var opener = new Opener();
        var controller = new DeviceController([new Source("usb", endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        var transport = opener.Last;

        controller.HandleDeviceRemoved(Removed(endpoint.Usb!.Path), metadataChanged: false);

        var reopening = controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopening);
        var startedFirst = await Task.WhenAny(transport!.Started.Task,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(transport.Started.Task, startedFirst);
        Assert.False(transport.Disposed.Task.IsCompleted);

        transport.Gate.SetResult();
        await lease.DisposeAsync();
        var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        opener.Last!.Gate.SetResult();
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task HandleDeviceRemoved_ConcurrentUserDispose_TeardownRunsOnceAndReservationReturns()
    {
        var endpoint = UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#race");
        var opener = new Opener();
        var controller = new DeviceController([new Source("usb", endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        var transport = opener.Last!;
        transport.Gate.SetResult();

        controller.HandleDeviceRemoved(Removed(endpoint.Usb!.Path), metadataChanged: false);
        var forced = lease.DisposeAsync();
        var user = lease.DisposeAsync();
        await forced;
        await user;

        Assert.Equal(1, transport.DisposeCount);
        var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        opener.Last!.Gate.SetResult();
        await reopened.DisposeAsync();
        Assert.NotSame(lease, reopened);
    }

    [Fact]
    public async Task Dispose_SlowLeaseTeardown_ReturnsWithoutBlockingCaller()
    {
        var endpoint = UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#sync");
        var opener = new Opener();
        var controller = new DeviceController([new Source("usb", endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        var transport = opener.Last!;

        var disposeCall = Task.Run(controller.Dispose);
        var finishedFirst = await Task.WhenAny(disposeCall, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(disposeCall, finishedFirst);
        await disposeCall;
        Assert.False(transport.Disposed.Task.IsCompleted);

        transport.Gate.SetResult();
        finishedFirst = await Task.WhenAny(transport.Disposed.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(transport.Disposed.Task, finishedFirst);
    }

    [Fact]
    public async Task DisposeAsync_PendingLeaseTeardown_AwaitsFullReleaseAndRejectsLaterRefresh()
    {
        var endpoint = UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#async");
        var opener = new Opener();
        var controller = new DeviceController([new Source("usb", endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        var transport = opener.Last!;

        var pending = controller.DisposeAsync();
        Assert.False(pending.IsCompleted);

        transport.Gate.SetResult();
        await pending;

        Assert.Equal(1, transport.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => controller.RefreshAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_ReleaseInProgressPlaceholder_IsReturnedOnlyAfterTransportTearsDown()
    {
        var endpoint = UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#placeholder");
        var opener = new Opener();
        var controller = new DeviceController([new Source("usb", endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var lease = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        var transport = opener.Last!;

        controller.HandleDeviceRemoved(Removed(endpoint.Usb!.Path), metadataChanged: false);
        transport.Gate.SetResult();
        await lease.DisposeAsync();
        var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);

        opener.Last!.Gate.SetResult();
        await reopened.DisposeAsync();
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, opener.Last!.DisposeCount);
    }

    [Fact]
    public void Dispose_StartWatchesAndOpenLeases_ReleasesWatchHandlesOnceAndRejectsLaterUse()
    {
        var watch = new WatchHandle();
        var controller = new DeviceController(
            [new Source("usb", UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#watch"))],
            new Opener(),
            DefaultDeviceSessionFactories.All,
            Kit(watch));

        controller.StartRemovalWatch();
        controller.Dispose();

        Assert.Equal(1, watch.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => controller.StartRemovalWatch());
        Assert.Throws<ObjectDisposedException>(() => controller.WatchUsb(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => controller.AdoptSession(
            new DeviceEndpoint { Source = "adb", Transport = "server", Address = "serial-watch" }, new object()));
        controller.Dispose();
        Assert.Equal(1, watch.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_RepeatedCalls_TeardownRunsOnce()
    {
        var endpoint = UsbEndpoint(@"\\?\usb#vid_18d1&pid_4ee7#twice");
        var opener = new Opener();
        var controller = new DeviceController([new Source("usb", endpoint)], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        var transport = opener.Last!;
        transport.Gate.SetResult();

        await controller.DisposeAsync();
        await controller.DisposeAsync();
        controller.Dispose();

        Assert.Equal(1, transport.DisposeCount);
    }

    private static DeviceEndpoint UsbEndpoint(string path) => new()
    {
        Source = "usb",
        Transport = nameof(UsbApiKind.Native),
        Address = path,
        Route = "direct-usb",
        Protocol = "adb",
        Usb = new(UsbApiKind.Native, path, 0x18D1, 0x4EE7, 0xFF, 0x42, 0x01)
    };

    private static UsbDeviceInfo Removed(string path) => new()
    {
        ApiName = "native",
        SourceApiKind = UsbApiKind.Native,
        DevicePath = path
    };

    private static ICommKit Kit(WatchHandle watch)
    {
        var kit = DispatchProxy.Create<ICommKit, KitProxy>();
        ((KitProxy)(object)kit).Watch = watch;
        return kit;
    }

    private sealed class Source(string name, DeviceEndpoint endpoint) : IDeviceDiscoverySource
    {
        public string Name => name;

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceEndpoint>>([endpoint]);
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

    private sealed class GatedTransport : IAsyncDisposable
    {
        private int _disposeCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) != 1)
            {
                return;
            }

            Started.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
            Disposed.TrySetResult();
        }
    }

    internal sealed class WatchHandle : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    public class KitProxy : DispatchProxy
    {
        internal WatchHandle? Watch { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ICommKit.MonitorUsbDevices))
            {
                return Watch;
            }

            throw new InvalidOperationException("Unexpected CommKit call.");
        }
    }
}