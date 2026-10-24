using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class DeviceEndpointBusyTests
{
    private static readonly DeviceEndpoint Usb = new()
    {
        Source = "usb",
        Transport = "Native",
        Address = "exact-key",
        Serial = "serial",
        Protocol = "adb",
        Route = "direct-usb",
        UsbInfo = new UsbDeviceInfo { SourceApiKind = UsbApiKind.Native, DeviceKey = "exact-key", SerialNumber = "serial" }
    };

    [Fact]
    public async Task OpenAsync_EndpointHeldByAnotherLease_ThrowsBusyException()
    {
        await using var controller = new DeviceController([new Source("usb", [Usb])], new RecordingOpener());
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = Assert.Single(snapshot.Endpoints);
        await using var held = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);

        var busy = await Assert.ThrowsAsync<DeviceEndpointBusyException>(
            () => controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken));

        Assert.Equal(endpoint.Id, busy.EndpointId);
        Assert.Equal(endpoint, busy.Endpoint);
        Assert.Equal(DeviceEndpointBusyException.DefaultMessage, busy.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(busy);
    }

    [Fact]
    public async Task OpenAsync_AfterHeldLeaseDisposed_ReopensSameEndpoint()
    {
        var opener = new RecordingOpener();
        await using var controller = new DeviceController([new Source("usb", [Usb])], opener);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = Assert.Single(snapshot.Endpoints);
        var held = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);
        await held.DisposeAsync();

        await using var reopened = await controller.OpenAsync(snapshot, endpoint.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, opener.Calls);
        Assert.Equal(endpoint, reopened.Endpoint);
    }

    [Fact]
    public void AcquireForMirror_BusyEndpointThenFree_RetriesUntilAcquired()
    {
        var attempts = 0;
        List<TimeSpan> delays = [];

        var result = DeviceMonitorService.AcquireForMirror(() =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new DeviceEndpointBusyException(new DeviceEndpointId("usb", "Native", "exact-key"));
            }

            return "lease";
        }, TimeSpan.FromSeconds(5), delays.Add);

        Assert.Equal("lease", result);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], delays);
    }

    [Fact]
    public void AcquireForMirror_EndpointStaysBusy_ThrowsActionableIOException()
    {
        var attempts = 0;

        var error = Assert.Throws<IOException>(() => DeviceMonitorService.AcquireForMirror<string>(() =>
        {
            attempts++;
            throw new DeviceEndpointBusyException(new DeviceEndpointId("usb", "Native", "exact-key"));
        }, TimeSpan.Zero, _ => { }));

        Assert.Equal(1, attempts);
        Assert.IsType<DeviceEndpointBusyException>(error.InnerException);
        Assert.Contains("busy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcquireForMirror_LeftoverOwnSessionReclaimed_RetriesWithoutDelay()
    {
        var attempts = 0;
        var reclaims = 0;
        List<TimeSpan> delays = [];

        var result = DeviceMonitorService.AcquireForMirror(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new DeviceEndpointBusyException(new DeviceEndpointId("usb", "Native", "exact-key"));
            }

            return "lease";
        }, TimeSpan.FromSeconds(5), delays.Add, () => reclaims++);

        Assert.Equal("lease", result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, reclaims);
        Assert.Empty(delays);
    }

    [Fact]
    public void AcquireForMirror_ReclaimNeverFreesEndpoint_ReclaimsOnceThenThrowsActionableIOException()
    {
        var attempts = 0;
        var reclaims = 0;

        var error = Assert.Throws<IOException>(() => DeviceMonitorService.AcquireForMirror<string>(() =>
        {
            attempts++;
            throw new DeviceEndpointBusyException(new DeviceEndpointId("usb", "Native", "exact-key"));
        }, TimeSpan.Zero, _ => { }, () => reclaims++));

        Assert.Equal(1, reclaims);
        Assert.Equal(2, attempts);
        Assert.IsType<DeviceEndpointBusyException>(error.InnerException);
        Assert.Contains("busy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsSamePhysicalTarget_AdbAndHdcInterfacesOfOneDevice_ReturnsTrue()
    {
        var adb = new DetectedDevice
        {
            Key = "usb-adb-key",
            Serial = "ADB0001",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0001"
        };
        var hdc = new DetectedDevice
        {
            Key = "usb-hdc-key",
            Serial = "HDC0001",
            Protocol = DeviceProtocolKind.Hdc,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0001"
        };

        Assert.True(DeviceMonitorService.IsSamePhysicalTarget(adb, hdc));
        Assert.True(DeviceMonitorService.IsSamePhysicalTarget(hdc, adb));
    }

    [Fact]
    public void IsSamePhysicalTarget_DifferentPhysicalIdentities_ReturnsFalse()
    {
        var left = new DetectedDevice
        {
            Key = "usb-adb-key",
            Serial = "ADB0001",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0001"
        };
        var right = new DetectedDevice
        {
            Key = "usb-adb-key-2",
            Serial = "ADB0002",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0002"
        };

        Assert.False(DeviceMonitorService.IsSamePhysicalTarget(left, right));
        Assert.False(DeviceMonitorService.IsSamePhysicalTarget(left, null!));
    }

    [Fact]
    public void CancelBackgroundEndpointWork_RegisteredWorkForSameDevice_CancelsItsToken()
    {
        using var monitor = DeviceMonitorService.Create();
        var device = new DetectedDevice
        {
            Key = "usb-adb-key",
            Serial = "ADB0001",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0001"
        };
        var other = new DetectedDevice
        {
            Key = "usb-adb-key-3",
            Serial = "ADB0003",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0003"
        };
        using var work = monitor.BeginBackgroundEndpointWork(device);
        using var unaffected = monitor.BeginBackgroundEndpointWork(other);

        var cancelled = monitor.CancelBackgroundEndpointWork(device);

        Assert.Equal(1, cancelled);
        Assert.True(work.Token.IsCancellationRequested);
        Assert.False(unaffected.Token.IsCancellationRequested);
    }

    [Fact]
    public void CancelBackgroundEndpointWork_AfterScopeDisposed_CancelsNothing()
    {
        using var monitor = DeviceMonitorService.Create();
        var device = new DetectedDevice
        {
            Key = "usb-adb-key",
            Serial = "ADB0001",
            Protocol = DeviceProtocolKind.Adb,
            Transport = DeviceTransportKind.Usb,
            PhysicalId = "physical-0001"
        };
        var work = monitor.BeginBackgroundEndpointWork(device);
        work.Dispose();

        Assert.Equal(0, monitor.CancelBackgroundEndpointWork(device));
    }

    private sealed class Source(string name, DeviceEndpoint[] endpoints) : IDeviceDiscoverySource
    {
        public string Name => name;
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceEndpoint>>(endpoints);
    }

    private sealed class RecordingOpener : IDeviceTransportOpener
    {
        public int Calls { get; private set; }

        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IAsyncDisposable>(new DisposableTransport());
        }
    }

    private sealed class DisposableTransport : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}