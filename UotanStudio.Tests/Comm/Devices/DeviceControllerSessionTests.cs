using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class DeviceControllerSessionTests
{
    private static DeviceEndpoint Endpoint(string protocol) => new()
    {
        Source = "adb-server@127.0.0.1:5037",
        Transport = "adb-server",
        Address = "serial-a",
        Serial = "serial-a",
        Route = "server",
        Protocol = protocol
    };

    private static UsbDeviceInfo UsbDetails() => new()
    {
        ApiName = "Native",
        SourceApiKind = UsbApiKind.Native,
        SourceDeviceType = "FakeUsbDevice",
        DevicePath = @"\\?\usb#vid_18d1&pid_4ee7",
        DeviceKey = "usb:bus-1:device-2",
        SerialNumber = "serial-a",
        ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        PhysicalPortPath = "usb-port-1-2",
        DeviceInstanceId = @"USB\VID_18D1&PID_4EE7\SERIAL-A",
        VendorId = 0x18D1,
        ProductId = 0x4EE7,
        InterfaceClass = 0xFF,
        InterfaceSubClass = 0x42,
        InterfaceProtocol = 0x01,
        InterfaceMetadataObserved = true,
        Speed = UsbDeviceSpeed.High,
        Interfaces =
        [
            new UsbInterfaceInfo
            {
                InterfaceNumber = 1,
                Class = 0xFF,
                SubClass = 0x42,
                Protocol = 0x01,
                Endpoints =
                [
                    new UsbEndpointInfo
                    {
                        EndpointAddress = 0x81,
                        Attributes = 2,
                        MaxPacketSize = 512,
                        Interval = 0
                    }
                ]
            }
        ]
    };

    private static DeviceEndpoint UsbEndpoint(UsbDeviceInfo info) => new()
    {
        Source = "usb",
        Transport = "Native",
        Address = info.DeviceKey,
        Serial = info.SerialNumber,
        Route = "direct-usb",
        Protocol = "adb",
        Usb = new UsbEndpointDetails(info.SourceApiKind, info.DevicePath, info.VendorId,
            info.ProductId, info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol),
        UsbInfo = info
    };

    [Fact]
    public void Equals_UsbInfoCopiedPerRead_EqualContentEndpointsStayEqualAndShareHash()
    {
        var left = UsbEndpoint(UsbDetails());
        var right = UsbEndpoint(UsbDetails());

        Assert.NotSame(left.UsbInfo!, right.UsbInfo!);
        Assert.True(left.Equals(right));
        Assert.True(left == right);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(left, left with { });

        var leased = new Dictionary<DeviceEndpoint, string>();
        leased[left] = "lease";
        Assert.Equal("lease", leased[right]);
    }

    [Fact]
    public void GetHashCode_RepeatedUsbInfoReads_HashSurvivesEveryFreshCopy()
    {
        var endpoint = UsbEndpoint(UsbDetails());

        Assert.NotSame(endpoint.UsbInfo!, endpoint.UsbInfo!);
        Assert.Equal(endpoint.GetHashCode(), endpoint.GetHashCode());
    }

    [Fact]
    public void Equals_UsbDetailsDifferInNestedEndpoint_TreatsEndpointsAsUnequal()
    {
        var left = UsbEndpoint(UsbDetails());
        var details = UsbDetails();
        details.Interfaces[0].Endpoints[0].MaxPacketSize = 1024;
        var right = UsbEndpoint(details);

        Assert.False(left.Equals(right));
        Assert.True(left != right);
    }

    [Fact]
    public async Task OpenAsync_RegisteredKind_ExposesTypedSessionOnLease()
    {
        var endpoint = Endpoint("adb");
        var session = new object();
        var controller = new DeviceController(
            [new Source([endpoint])],
            new Opener(new MarkerTransport()),
            [new FixedFactory(DeviceSessionKind.Adb, session)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);

        await using var lease = await controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Adb,
            TestContext.Current.CancellationToken);

        Assert.Same(session, lease.Session);
        Assert.True(lease.TryGetSession<object>(out var resolved));
        Assert.Same(session, resolved);
        Assert.False(lease.TryGetSession<string>(out _));
    }

    [Fact]
    public async Task DisposeAsync_SessionLease_DisposesSessionBeforeTransport()
    {
        var log = new List<string>();
        var endpoint = Endpoint("adb");
        var controller = new DeviceController(
            [new Source([endpoint])],
            new Opener(new OrderedTransport(log)),
            [new FixedFactory(DeviceSessionKind.Adb, new SessionMarker(log))]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);

        var lease = await controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Adb,
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        Assert.Equal(["session", "transport"], log);
        await using var reopened = await controller.OpenAsync(snapshot, endpoint.Id,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OpenAsync_UnregisteredKind_DisposesTransportAndReleasesReservation()
    {
        var transport = new MarkerTransport();
        var endpoint = Endpoint("adb");
        var controller = new DeviceController([new Source([endpoint])], new Opener(transport));
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotSupportedException>(() => controller.OpenAsync(snapshot, endpoint.Id,
            DeviceSessionKind.Mtk, TestContext.Current.CancellationToken));
        Assert.True(transport.Disposed);

        await using var plain = await controller.OpenAsync(snapshot, endpoint.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(plain.Session);
    }

    [Fact]
    public async Task CreateAsync_FastbootKindOnAdbEndpoint_RejectsBeforeTouchingTransport()
    {
        var transport = new MarkerTransport();
        var factory = new FastbootDeviceSessionFactory();

        await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateAsync(
            Endpoint("adb"), transport, TestContext.Current.CancellationToken));
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task CreateAsync_AdbKindOnServerEndpoint_AdoptsOpenedServerSession()
    {
        var session = new AdbSession(new AdbServerServices("serial-a", "127.0.0.1", 5037));
        var factory = new AdbDeviceSessionFactory();

        var created = await factory.CreateAsync(Endpoint("adb"),
            new OwnedDeviceTransport<AdbSession>(session), TestContext.Current.CancellationToken);

        Assert.Same(session, created);
        session.Dispose();
    }

    [Fact]
    public async Task CreateAsync_MissingProtocolObservation_RejectsEveryDefaultFactory()
    {
        var endpoint = Endpoint(protocol: string.Empty);
        var transport = new MarkerTransport();
        foreach (var factory in DefaultDeviceSessionFactories.All)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateAsync(
                endpoint, transport, TestContext.Current.CancellationToken));
        }

        Assert.False(transport.Disposed);
    }

    private sealed class Source(IReadOnlyList<DeviceEndpoint> endpoints) : IDeviceDiscoverySource
    {
        public string Name => "adb-server@127.0.0.1:5037";
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(endpoints);
    }

    private sealed class Opener(IAsyncDisposable transport) : IDeviceTransportOpener
    {
        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken) =>
            Task.FromResult(transport);
    }

    private sealed class FixedFactory(DeviceSessionKind kind, object session) : IDeviceSessionFactory
    {
        public DeviceSessionKind Kind => kind;
        public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport,
            CancellationToken cancellationToken) => Task.FromResult(session);
    }

    private sealed class MarkerTransport : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderedTransport(List<string> log) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            log.Add("transport");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SessionMarker(List<string> log) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            log.Add("session");
            return ValueTask.CompletedTask;
        }
    }
}