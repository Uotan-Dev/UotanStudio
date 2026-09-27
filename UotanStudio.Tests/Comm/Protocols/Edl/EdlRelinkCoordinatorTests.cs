using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Reenum;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;
using UotanStudio.Comm.Transports.Usb.Core;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlRelinkCoordinatorTests
{
    private static readonly Guid Container = Guid.Parse("53ef3b77-d4c4-4618-9251-d302f3bd4b20");
    private static readonly Guid OtherContainer = Guid.Parse("11ef3b77-d4c4-4618-9251-d302f3bd4b11");

    [Fact]
    public async Task RelinkAsync_SameContainerNewPidEndpoint_LeasesUniqueReplacement()
    {
        var previous = Endpoint("old", Container, port: null);
        var replacement = Endpoint("new", Container, port: null, pid: 0x2003);
        var decoy = Endpoint("decoy", OtherContainer, port: null, pid: 0x2003);
        var source = new Source([previous], [], [decoy, replacement]);
        var opener = new Opener();
        var controller = new DeviceController([source], opener, [new Factory(DeviceSessionKind.Edl)]);

        await using var lease = await EdlRelinkCoordinator.Instance.RelinkAsync(controller,
            Offer(previous, replacementEvidence: true), DeviceSessionKind.Edl,
            TestContext.Current.CancellationToken);

        Assert.Equal(replacement.Id, lease.Endpoint.Id);
        Assert.Equal([replacement.Id], opener.Opened);
    }

    [Fact]
    public async Task RelinkAsync_PortOnlyEvidence_UsesPhysicalPortPolicy()
    {
        var previous = Endpoint("old", container: null, port: @"2\1\3");
        var replacement = Endpoint("new", container: null, port: @"2\1\3", pid: 0x2003);
        var controller = new DeviceController([new Source([previous], [], [replacement])], new Opener(),
            [new Factory(DeviceSessionKind.Edl)]);

        await using var lease = await EdlRelinkCoordinator.Instance.RelinkAsync(controller,
            Offer(previous, replacementEvidence: true), DeviceSessionKind.Edl,
            TestContext.Current.CancellationToken);

        Assert.Equal(replacement.Id, lease.Endpoint.Id);
    }

    [Fact]
    public async Task RelinkAsync_OnlyForeignContainerAppears_TimesOutWithoutOpen()
    {
        var previous = Endpoint("old", Container, port: null);
        var stranger = Endpoint("stranger", OtherContainer, port: null, pid: 0x2003);
        var opener = new Opener();
        var controller = new DeviceController([new Source([previous], [], [stranger])], opener,
            [new Factory(DeviceSessionKind.Edl)]);
        var offer = Offer(previous, replacementEvidence: true) with { Timeout = TimeSpan.FromMilliseconds(100) };

        await Assert.ThrowsAsync<TimeoutException>(() => EdlRelinkCoordinator.Instance.RelinkAsync(controller,
            offer, DeviceSessionKind.Edl, TestContext.Current.CancellationToken));

        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task RelinkAsync_TwoSameContainerCandidates_RejectsAmbiguityBeforeOpen()
    {
        var previous = Endpoint("old", Container, port: null);
        var first = Endpoint("first", Container, port: null, pid: 0x2003);
        var second = Endpoint("second", Container, port: null, pid: 0x2003);
        var opener = new Opener();
        var controller = new DeviceController([new Source([previous], [], [first, second])], opener,
            [new Factory(DeviceSessionKind.Edl)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => EdlRelinkCoordinator.Instance.RelinkAsync(
            controller, Offer(previous, replacementEvidence: true), DeviceSessionKind.Edl,
            TestContext.Current.CancellationToken));

        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task RelinkOffer_WithoutCorrelationEvidence_RefusesBeforeDiscovery()
    {
        var previous = Endpoint("old", container: null, port: null);
        var source = new Source([previous]);
        var controller = new DeviceController([source], new Opener(), [new Factory(DeviceSessionKind.Edl)]);
        var offer = Offer(previous, replacementEvidence: false);

        await Assert.ThrowsAsync<NotSupportedException>(() => EdlRelinkCoordinator.Instance.RelinkAsync(
            controller, offer, DeviceSessionKind.Edl, TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task RelinkAsync_OfferWithoutPreviousEndpoint_FailsBeforeControllerUse()
    {
        var previous = Endpoint("old", Container, port: null);
        var source = new Source([previous]);
        var controller = new DeviceController([source], new Opener(), [new Factory(DeviceSessionKind.Edl)]);
        var offer = Offer(previous, replacementEvidence: true) with { PreviousEndpoint = null };

        await Assert.ThrowsAsync<ArgumentNullException>(() => EdlRelinkCoordinator.Instance.RelinkAsync(
            controller, offer, DeviceSessionKind.Edl, TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task RelinkAsync_PreviousLeaseStillActive_RefusesUntilReleased()
    {
        var previous = Endpoint("old", "edl", Container, port: null);
        var replacement = Endpoint("new", "edl", Container, port: null, pid: 0x2003);
        var controller = new DeviceController([new Source([previous], [], [replacement])], new Opener(),
            [new Factory(DeviceSessionKind.Edl)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await using var old = await controller.OpenAsync(snapshot, previous.Id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => EdlRelinkCoordinator.Instance.RelinkAsync(
            controller, Offer(previous, replacementEvidence: true), DeviceSessionKind.Edl,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RelinkAsync_OutsideTimeoutRange_ThrowsArgumentOutOfRange()
    {
        var previous = Endpoint("old", Container, port: null);
        var controller = new DeviceController([new Source([previous])], new Opener(),
            [new Factory(DeviceSessionKind.Edl)]);
        var offer = Offer(previous, replacementEvidence: true) with { Timeout = TimeSpan.Zero };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EdlRelinkCoordinator.Instance.RelinkAsync(
            controller, offer, DeviceSessionKind.Edl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_ProgrammerUploadFailure_PublishesRelinkOfferCarryingLastIdentity()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueCommandReady(transport);
        EnqueueIdentityResponse(transport, 1, [1, 0, 0, 0]);
        EnqueueIdentityResponse(transport, 2, [7, 0, 0, 0]);
        EnqueueIdentityResponse(transport, 3, new byte[48]);
        EnqueueIdentityResponse(transport, 7, [3, 0, 0, 0]);
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, status: 1);
        using var session = new EdlSession(transport, new FirehoseConfiguration
        {
            AutoDetectSectorSize = false,
            SectorSize = 512
        });
        var endpoint = Endpoint("old", "edl", Container, port: @"2\1\3");
        session.AttachRelinkContext(endpoint, new TransportGeneration(4), TimeSpan.FromSeconds(45));
        using var image = new MemoryStream(new byte[16]);

        await Assert.ThrowsAsync<SaharaException>(() => session.InitializeAsync(image,
            new EdlInitializationOptions { QueryIdentityBeforeUpload = true },
            TestContext.Current.CancellationToken));

        Assert.NotNull(session.LastRelinkOffer);
        var offer = session.LastRelinkOffer!;
        Assert.Equal(EdlLinkStage.Failed, session.LinkStage);
        Assert.Equal(endpoint.Id, offer.PreviousEndpoint!.Id);
        Assert.Equal(7ul, offer.LastIdentity?.HardwareId);
        Assert.Equal(new TransportGeneration(4), offer.ObservedGeneration);
        Assert.Equal(ModeTransitionEvidence.Container, offer.Correlation);
        Assert.Equal(TimeSpan.FromSeconds(45), offer.Timeout);
        Assert.NotNull(offer.Identity);
        Assert.Equal(Container, offer.Identity!.ContainerId);
    }

    [Fact]
    public async Task InitializeAsync_FailureWithoutAttachedContext_LeavesOfferAbsentButStageFailed()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, status: 1);
        using var session = new EdlSession(transport, new FirehoseConfiguration
        {
            AutoDetectSectorSize = false,
            SectorSize = 512
        });
        using var image = new MemoryStream(new byte[16]);

        await Assert.ThrowsAnyAsync<SaharaException>(() => session.InitializeAsync(image,
            TestContext.Current.CancellationToken));

        Assert.Null(session.LastRelinkOffer);
        Assert.Equal(EdlLinkStage.Failed, session.LinkStage);
    }

    [Fact]
    public async Task RelinkOffer_PublishesStaleAgainWhenCommandsContinueAfterFailure()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, status: 1);
        using var session = new EdlSession(transport, new FirehoseConfiguration
        {
            AutoDetectSectorSize = false,
            SectorSize = 512
        });
        var endpoint = Endpoint("old", "edl", Container, port: null);
        session.AttachRelinkContext(endpoint);
        using var image = new MemoryStream(new byte[16]);
        await Assert.ThrowsAnyAsync<SaharaException>(() => session.InitializeAsync(image,
            TestContext.Current.CancellationToken));

        var error = await Assert.ThrowsAsync<EdlException>(() => session.FlashPartitionAsync(image, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ConnectionLost, error.ErrorCode);
        Assert.Equal(EdlLinkStage.Stale, session.LinkStage);
        Assert.NotNull(session.LastRelinkOffer);
        Assert.Equal(endpoint.Id, session.LastRelinkOffer.PreviousEndpoint!.Id);
    }

    private static EdlRelinkOffer Offer(DeviceEndpoint previous, bool replacementEvidence) => new()
    {
        PreviousEndpoint = previous,
        Identity = previous.UsbInfo is { } info ? PhysicalDeviceIdentity.From(info) : null,
        LastIdentity = null,
        Timeout = TimeSpan.FromSeconds(5),
        Correlation = replacementEvidence
            ? PhysicalDeviceIdentity.From(previous.UsbInfo!)?.ContainerId is not null
                ? ModeTransitionEvidence.Container
                : ModeTransitionEvidence.PhysicalPort
            : null
    };

    private static DeviceEndpoint Endpoint(string path, Guid? container, string? port, ushort pid = 0x2002) =>
        Endpoint(path, "edl", container, port, pid);

    private static DeviceEndpoint Endpoint(string path, string protocol, Guid? container, string? port,
        ushort pid = 0x2002)
    {
        var info = new UsbDeviceInfo
        {
            ApiName = "test",
            SourceApiKind = UsbApiKind.Custom,
            DevicePath = path,
            ContainerId = container,
            PhysicalPortPath = port,
            VendorId = 0x05C6,
            ProductId = pid,
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

    private static void EnqueueHello(ScriptedCommTransport transport) => transport.Enqueue(BuildHelloPacket());

    private static byte[] BuildHelloPacket() => StructToBytes(new SaharaHelloPacket
    {
        Command = SaharaCommand.Hello,
        Length = (uint)Marshal.SizeOf<SaharaHelloPacket>(),
        Version = EdlConstants.SaharaVersion,
        MinVersion = 1,
        MaxCommandLength = 3,
        Mode = SaharaMode.ImageTxPending
    });

    private static void EnqueueCommandReady(ScriptedCommTransport transport) => transport.Enqueue(StructToBytes(
        new SaharaPacketHeader
        {
            Command = SaharaCommand.CommandReady,
            Length = (uint)Marshal.SizeOf<SaharaPacketHeader>()
        }));

    private static void EnqueueEndOfImage(ScriptedCommTransport transport, uint status) => transport.Enqueue(
        StructToBytes(new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = (uint)Marshal.SizeOf<SaharaEndOfImagePacket>(),
            ImageId = (uint)SaharaImageId.Firehose,
            Status = status
        }));

    private static void EnqueueIdentityResponse(ScriptedCommTransport transport, uint command, byte[] bytes)
    {
        Span<byte> header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)SaharaCommand.ExecuteResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], command);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)bytes.Length);
        transport.Enqueue(header.ToArray());
        transport.Enqueue(bytes);
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), fDeleteOld: false);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
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

        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(endpoint.Id);
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
            var session = new UsbDeviceSession(info.ApiName, info.SourceApiKind, device);
            return Task.FromResult<IAsyncDisposable>(new OwnedDeviceTransport<IUsbDeviceSession>(session));
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
        public void Dispose() { }
    }

    private sealed class FakeUsbDevice : UsbDevice
    {
        protected override string BackendName => "test";
        protected override bool IsOpen => true;
        internal override bool IsHandleOpen => true;
        protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs) =>
            throw new InvalidOperationException("Relinking must not read protocol bytes.");
        protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs) =>
            throw new InvalidOperationException("Relinking must not write protocol bytes.");
        public override byte[] Read(int length) => throw new InvalidOperationException();
        public override long Write(byte[] data, int length) => throw new InvalidOperationException();
        public override int GetSerialNumber() => 0;
        public override int CreateHandle() => 0;
        public override void Reset() => throw new InvalidOperationException();
        public override void Dispose() { }
    }
}