using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Core;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed partial class DeviceReconnectionTests
{
    private static readonly Guid Container = Guid.Parse("53ef3b77-d4c4-4618-9251-d302f3bd4b20");

    [Theory]
    [InlineData(DeviceSessionKind.Mtk, "mtk")]
    [InlineData(DeviceSessionKind.Edl, "edl")]
    [InlineData(DeviceSessionKind.Fastboot, "fastboot")]
    public async Task WaitForUsbReconnectAsync_ModeChanged_OpensOnlyMatchingContainer(DeviceSessionKind kind, string protocol)
    {
        var previous = Endpoint("old", "mtk");
        var target = Endpoint("new", protocol);
        var peer = Endpoint("peer", protocol, Guid.NewGuid());
        var source = new Source([previous], [peer], [peer, target]);
        var opener = new Opener();
        var factory = new Factory(kind);
        var controller = new DeviceController([source], opener, [factory]);

        await using var lease = await controller.WaitForUsbReconnectAsync(previous, kind,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(target.Id, lease.Endpoint.Id);
        Assert.Equal(new[] { target.Id }, opener.Opened);
        Assert.Equal(3, source.Calls);
        Assert.Equal(1, factory.Calls);
        Assert.All(opener.Devices, device => Assert.Equal(0, device.IoCalls));
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_SameEndpointReturns_RequiresObservedDisappearance()
    {
        var previous = Endpoint("same", "mtk");
        var source = new Source([previous], [], [previous]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await using var lease = await controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, source.Calls);
        Assert.Equal(previous.Id, lease.Endpoint.Id);
        Assert.Single(opener.Opened);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("other-container")]
    [InlineData("missing-container")]
    [InlineData("different-backend")]
    [InlineData("wrong-protocol")]
    [InlineData("server")]
    public async Task WaitForUsbReconnectAsync_NoVerifiedTransition_TimesOutWithoutOpen(string failure)
    {
        var previous = Endpoint("old", "mtk");
        var target = Endpoint("new", "mtk");
        switch (failure)
        {
            case "unchanged": target = previous; break;
            case "other-container": target = Endpoint("new", "mtk", Guid.NewGuid()); break;
            case "missing-container":
                var missing = target.UsbInfo!;
                missing.ContainerId = null;
                target = target with { UsbInfo = missing };
                break;
            case "different-backend": target = target with { Transport = UsbApiKind.LibUsbDotNet.ToString() }; break;
            case "wrong-protocol": target = target with { Protocol = "edl" }; break;
            case "server": target = target with { Route = "server" }; break;
        }
        var source = new Source([target]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<TimeoutException>(() => controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            100, TestContext.Current.CancellationToken));

        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_TwoMatchingInterfaces_RejectsAmbiguityBeforeOpen()
    {
        var previous = Endpoint("old", "mtk");
        var source = new Source([Endpoint("one", "mtk"), Endpoint("two", "mtk")]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(opener.Opened);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("path")]
    [InlineData("pid")]
    [InlineData("backend")]
    [InlineData("key")]
    [InlineData("interface")]
    public async Task WaitForUsbReconnectAsync_IdentityChangesDuringOpen_DisposesBeforeSessionCreation(string failure)
    {
        var previous = Endpoint("old", "mtk");
        var target = Endpoint("new", "mtk");
        var opener = new Opener
        {
            ChangeOpenedInfo = info =>
            {
                switch (failure)
                {
                    case "container": info.ContainerId = Guid.NewGuid(); break;
                    case "path": info.DevicePath = "other"; break;
                    case "pid": info.ProductId++; break;
                    case "backend": info.SourceApiKind = UsbApiKind.LibUsbDotNet; break;
                    case "key": info.DeviceKey = "other"; break;
                    case "interface": info.InterfaceClass = 0; break;
                }
            }
        };
        var factory = new Factory(DeviceSessionKind.Mtk);
        var controller = new DeviceController([new Source([target])], opener, [factory]);

        await Assert.ThrowsAsync<IOException>(() => controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, factory.Calls);
        Assert.Equal(1, Assert.Single(opener.Devices).DisposeCount);
        Assert.Single(opener.Opened);
        opener.ChangeOpenedInfo = null;
        await using var recovered = await controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, recovered.Endpoint.Id);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_OldLeaseActive_RejectsUntilReleased()
    {
        var previous = Endpoint("old", "mtk");
        var source = new Source([previous], [Endpoint("new", "mtk")]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        var old = await controller.OpenAsync(snapshot, previous.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, source.Calls);
        Assert.Single(opener.Opened);
        await old.DisposeAsync();
        await using var lease = await controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, opener.Opened.Count);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_PreviousContainerMissing_RejectsWithoutDiscovery()
    {
        var previous = Endpoint("old", "mtk");
        var info = previous.UsbInfo!;
        info.ContainerId = null;
        previous = previous with { UsbInfo = info };
        var source = new Source([]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        await Assert.ThrowsAsync<NotSupportedException>(() => controller.WaitForUsbReconnectAsync(previous, DeviceSessionKind.Mtk,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Calls);
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_OpenFails_DoesNotRetryOrChangeRoute()
    {
        var source = new Source([Endpoint("new", "mtk")]);
        var failure = new IOException("Device unavailable.");
        var opener = new Opener { Failure = failure };
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);

        var actual = await Assert.ThrowsAsync<IOException>(() => controller.WaitForUsbReconnectAsync(Endpoint("old", "mtk"),
            DeviceSessionKind.Mtk, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        Assert.Single(opener.Opened);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_CancelledAfterSessionCreation_ReleasesSessionAndReservation()
    {
        var source = new Source([Endpoint("new", "mtk")]);
        var opener = new Opener();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var factory = new Factory(DeviceSessionKind.Mtk) { OnCreate = cancellation.Cancel };
        var controller = new DeviceController([source], opener, [factory]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.WaitForUsbReconnectAsync(Endpoint("old", "mtk"),
            DeviceSessionKind.Mtk, cancellationToken: cancellation.Token));

        Assert.Equal(1, Assert.Single(factory.Sessions).DisposeCount);
        Assert.Equal(1, Assert.Single(opener.Devices).DisposeCount);
        factory.OnCreate = null;
        await using var recovered = await controller.WaitForUsbReconnectAsync(Endpoint("old", "mtk"),
            DeviceSessionKind.Mtk, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForUsbReconnectAsync_PreCancelled_DoesNotDiscoverOrOpen()
    {
        var source = new Source([]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Mtk)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.WaitForUsbReconnectAsync(Endpoint("old", "mtk"),
            DeviceSessionKind.Mtk, cancellationToken: cancellation.Token));

        Assert.Equal(0, source.Calls);
        Assert.Empty(opener.Opened);
    }

    private static DeviceEndpoint Endpoint(string path, string protocol, Guid? container = null)
    {
        var info = new UsbDeviceInfo
        {
            ApiName = "test",
            SourceApiKind = UsbApiKind.Custom,
            DevicePath = path,
            ContainerId = container ?? Container,
            VendorId = 0x0E8D,
            ProductId = protocol == "mtk" ? (ushort)0x2001 : (ushort)0x2002,
            SerialNumber = "identical-serial",
            InterfaceClass = 0xFF,
            InterfaceSubClass = 0x42,
            InterfaceProtocol = 3
        };
        info.DeviceKey = UsbDeviceIdentity.BuildKey(info);
        return new DeviceEndpoint
        {
            Source = UsbDiscoverySource.SourceName,
            Transport = UsbApiKind.Custom.ToString(),
            Address = info.DeviceKey,
            Serial = info.SerialNumber,
            Protocol = protocol,
            Route = "direct-usb",
            UsbInfo = info,
            Usb = new(info.SourceApiKind, path, info.VendorId, info.ProductId,
                info.InterfaceClass, info.InterfaceSubClass, info.InterfaceProtocol)
        };
    }

    private sealed class Source(params IReadOnlyList<DeviceEndpoint>[] snapshots) : IDeviceDiscoverySource
    {
        public string Name => UsbDiscoverySource.SourceName;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshots[Math.Min(Calls++, snapshots.Length - 1)]);
        }
    }

    private sealed class Opener : IDeviceTransportOpener
    {
        public List<DeviceEndpointId> Opened { get; } = [];
        public List<FakeUsbDevice> Devices { get; } = [];
        public Action<UsbDeviceInfo>? ChangeOpenedInfo { get; set; }
        public Exception? Failure { get; init; }

        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(endpoint.Id);
            if (Failure is not null)
            {
                return Task.FromException<IAsyncDisposable>(Failure);
            }
            var info = endpoint.UsbInfo!;
            var device = new FakeUsbDevice
            {
                DevicePath = info.DevicePath,
                ContainerId = info.ContainerId,
                PhysicalPortPath = info.PhysicalPortPath,
                VendorId = info.VendorId,
                ProductId = info.ProductId,
                SerialNumber = info.SerialNumber,
                InterfaceClass = info.InterfaceClass,
                InterfaceSubClass = info.InterfaceSubClass,
                InterfaceProtocol = info.InterfaceProtocol
            };
            Devices.Add(device);
            var session = new UsbDeviceSession(info.ApiName, info.SourceApiKind, device);
            ChangeOpenedInfo?.Invoke(session.DeviceInfo);
            return Task.FromResult<IAsyncDisposable>(new OwnedDeviceTransport<IUsbDeviceSession>(session));
        }
    }

    private sealed class Factory(DeviceSessionKind kind) : IDeviceSessionFactory
    {
        public DeviceSessionKind Kind => kind;
        public int Calls { get; private set; }
        public Action? OnCreate { get; set; }
        public List<Session> Sessions { get; } = [];
        public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
        {
            Calls++;
            var session = new Session();
            Sessions.Add(session);
            OnCreate?.Invoke();
            return Task.FromResult<object>(session);
        }
    }

    private sealed class Session : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeUsbDevice : UsbDevice
    {
        public int DisposeCount { get; private set; }
        public int IoCalls { get; private set; }
        protected override string BackendName => "test";
        protected override bool IsOpen => true;
        internal override bool IsHandleOpen => true;
        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
        {
            IoCalls++;
            throw new InvalidOperationException("Reconnection must not read protocol bytes.");
        }
        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
        {
            IoCalls++;
            throw new InvalidOperationException("Reconnection must not write protocol bytes.");
        }
        public override byte[] Read(int length) => throw new InvalidOperationException();
        public override long Write(byte[] data, int length) => throw new InvalidOperationException();
        public override int GetSerialNumber() => 0;
        public override int CreateHandle() => 0;
        public override void Reset() => throw new InvalidOperationException();
        public override void Dispose() => DisposeCount++;
    }
}