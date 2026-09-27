using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBootstrapAsync_NorSelection_IgnoresAbsentEmmcAndRetainsNorInventory(bool nand64)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport, nand64);

        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        var storage = Assert.IsType<MtkLegacyStorageInfo>(session.LegacyStorage);
        Assert.Equal(MtkXFlashStorageKind.Nor, storage.Kind);
        Assert.Empty(storage.Regions);
        Assert.Empty(storage.CidHex);
        Assert.Empty(storage.FirmwareRevisionHex);
        var nor = Assert.IsType<MtkLegacyNorGeometry>(storage.Nor);
        Assert.Equal(8192, nor.CapacityBytes);
        Assert.Equal(0x1234, nor.FlashId);
        Assert.Equal(Convert.ToHexString(LegacyNorInventory()), nor.InventoryHex);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("status")]
    [InlineData("completion")]
    public async Task LegacyBootstrapAsync_InvalidNorOrCompletion_DisposesWithoutFallingBackToEmmc(string failure)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var nor = LegacyNorInventory();
        if (failure == "absent")
        {
            nor.AsSpan(8, 4).Clear();
        }
        if (failure == "status")
        {
            nor[3] = 1;
        }
        EnqueueLegacyBootstrap(transport, false, failure, norInventory: nor);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, LegacyBootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nor },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(MtkXmlHostOs.Linux, 0x0C)]
    [InlineData(MtkXmlHostOs.Windows, 0x0B)]
    public async Task ReadLegacyNorStorageAsync_UnalignedRange_UsesNorHeaderWithoutPartitionSwitch(MtkXmlHostOs host, byte hostByte)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport, host: host);
        transport.Enqueue([0x5A, 1, 0x5A]);
        transport.Enqueue(Enumerable.Repeat((byte)1, 512).ToArray());
        transport.Enqueue([2, 0, 2, 3, 0, 5]);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.ReadLegacyNorStorageAsync(3, 514, destination, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        var header = Convert.FromHexString("D60C000000000000000003000000000000020200000200");
        header[1] = hostByte;
        Assert.Equal(new byte[] { 0x72 }, transport.Writes[before]);
        Assert.Equal(header, transport.Writes[before + 1]);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.All(transport.Writes.Skip(before + 2), bytes => Assert.Equal(new byte[] { 0x5A }, bytes));
        Assert.Equal(Enumerable.Repeat((byte)1, 512).Concat(new byte[] { 2, 3 }), destination.ToArray());
        Assert.Equal(new long[] { 512, 514 }, progress.Values);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task WriteLegacyNorStorageAsync_TwoPackets_UsesNorSelectorWithoutPaddingOrPartitionSwitch()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue([0x5A, 0x69, 0x69]);
        using var source = new FragmentedSource(Enumerable.Repeat((byte)0xFF, 1024).ToArray());
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.WriteLegacyNorStorageAsync(512, 1024, source, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("6220080000000000000200000000000000040000000200"), transport.Writes[before]);
        Assert.Equal(7, transport.Writes.Count - before);
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 512), transport.Writes[before + 2]);
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 512), transport.Writes[before + 5]);
        Assert.Equal(new byte[] { 0xFE, 0 }, transport.Writes[before + 3]);
        Assert.Equal(new byte[] { 0xFE, 0 }, transport.Writes[before + 6]);
        Assert.Equal(new long[] { 512, 1024 }, progress.Values);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNorStorageAsync_BadPacket_FaultsWithoutAcknowledgingSuccess(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        using var stream = new MemoryStream(new byte[512]);
        using var destination = new MemoryStream();
        if (write)
        {
            transport.Enqueue([0x5A, 0xA5]);
        }
        else
        {
            transport.Enqueue([0x5A, 1, 0x5A]);
            transport.Enqueue(new byte[512]);
            transport.Enqueue([0, 1]);
        }
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => write
            ? session.WriteLegacyNorStorageAsync(0, 512, stream, progress: progress, cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadLegacyNorStorageAsync(0, 512, destination, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        Assert.Empty(progress.Values);
        Assert.Equal(write ? 4 : 2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData(-1, 512, false)]
    [InlineData(0, 0, false)]
    [InlineData(8192, 1, false)]
    [InlineData(8191, 2, false)]
    [InlineData(long.MaxValue, 2, false)]
    [InlineData(0, long.MaxValue, false)]
    [InlineData(1, 512, true)]
    [InlineData(0, 511, true)]
    [InlineData(8192, 512, true)]
    public async Task LegacyNorStorageAsync_InvalidRange_RejectsBeforeIo(long offset, long length, bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        using var stream = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => write
            ? session.WriteLegacyNorStorageAsync(offset, length, stream, cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadLegacyNorStorageAsync(offset, length, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("erase")]
    public async Task LegacyEmmcStorageAsync_NorSession_RejectsBeforeIo(string operation)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        using var stream = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => operation switch
        {
            "read" => session.ReadLegacyStorageAsync(8, 0, 512, stream, cancellationToken: TestContext.Current.CancellationToken),
            "write" => session.WriteLegacyStorageAsync(8, 0, 512, stream, cancellationToken: TestContext.Current.CancellationToken),
            _ => session.EraseLegacyStorageAsync(8, 0, 512, cancellationToken: TestContext.Current.CancellationToken)
        });

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNorStorageAsync_EmmcSession_RejectsBeforeIo(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        using var stream = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => write
            ? session.WriteLegacyNorStorageAsync(0, 512, stream, cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadLegacyNorStorageAsync(0, 512, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNorStorageAsync_Cancellation_DistinguishesPreIoAndInFlight(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.WriteLegacyNorStorageAsync(0, 512, source, cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNorStorageAsync_BlockedCallerStream_WholeTransferDeadlineFaults(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(write ? [0x5A] : [0x5A, 1, 0x5A]);
        if (!write)
        {
            transport.Enqueue(new byte[512]);
            transport.Enqueue([0, 0]);
        }
        using var stream = new DeadlineStorageStream();

        await Assert.ThrowsAsync<TimeoutException>(() => write
            ? session.WriteLegacyNorStorageAsync(0, 512, stream, timeoutMs: 100,
                cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadLegacyNorStorageAsync(0, 512, stream, timeoutMs: 100,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNorStorageAsync_RejectedHeader_DoesNotTransferData(bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(write ? [0xA5] : [0x5A, 1, 0xA5]);
        using var stream = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => write
            ? session.WriteLegacyNorStorageAsync(0, 512, stream, cancellationToken: TestContext.Current.CancellationToken)
            : session.ReadLegacyNorStorageAsync(0, 512, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(write ? 1 : 2, transport.Writes.Count - before);
        Assert.Equal(0, stream.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteLegacyNorStorageAsync_ShortSource_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        using var source = new MemoryStream(new byte[511]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteLegacyNorStorageAsync(0, 512, source,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task LegacyNorStorageAsync_ConcurrentRead_WaitsForWriteCompletion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue([0x5A, 0x69, 0x5A, 1, 0x5A, 7, 0, 7]);
        using var source = new PausedStorageSource();
        using var destination = new MemoryStream();
        var write = session.WriteLegacyNorStorageAsync(0, 512, source, cancellationToken: TestContext.Current.CancellationToken);
        Task read;
        try
        {
            await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var before = transport.Writes.Count;
            read = session.ReadLegacyNorStorageAsync(8191, 1, destination, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(read.IsCompleted);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        await read;
        Assert.Equal(new byte[] { 7 }, destination.ToArray());
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(29)]
    public void LegacyNorGeometry_InvalidLength_Rejects(int length)
    {
        Assert.Throws<MtkProtocolException>(() => MtkLegacyStorageInfo.ParseNor(new byte[length]));
    }

    [Fact]
    public void LegacyNorGeometry_MaximumCapacity_DoesNotSignExtendOrIncludeOtp()
    {
        var bytes = LegacyNorInventory();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), uint.MaxValue);

        var info = MtkLegacyStorageInfo.ParseNor(bytes);

        Assert.Equal(uint.MaxValue, info.Nor!.CapacityBytes);
        info.Nor.ValidateRange(uint.MaxValue - 1L, 1, false);
        Assert.Throws<ArgumentOutOfRangeException>(() => info.Nor.ValidateRange(uint.MaxValue, 1, false));
    }

    private static byte[] LegacyNorInventory()
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), 0x1234);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 8192);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 0x12345678);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), uint.MaxValue);
        return bytes;
    }

    private static async Task<MtkSession> CreateLegacyNorSessionAsync(ScriptedCommTransport transport,
        bool nand64 = false, MtkXmlHostOs host = MtkXmlHostOs.Linux)
    {
        using var dir = new TempDirectory();
        var emmc = new byte[92];
        emmc[3] = 1;
        EnqueueLegacyBootstrap(transport, nand64, norInventory: LegacyNorInventory(), emmcInventory: emmc);
        return await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir) with
            {
                StorageKind = MtkXFlashStorageKind.Nor,
                Legacy = LegacyProfile(nand64) with { HostOs = host }
            }, TestContext.Current.CancellationToken);
    }
}