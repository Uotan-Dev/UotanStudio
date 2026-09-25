using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Adb;
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