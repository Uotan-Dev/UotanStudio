using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(256)]
    [InlineData(65536)]
    public async Task DiscoverXFlashStorageAsync_NorGeometry_UsesNorQueryAndWholeDeviceRegion(int pageSize)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, NorGeometry(pageSize));
        EnqueueControlReply(transport, NorPacketLengths(Math.Max(pageSize, 512)));
        var before = transport.Writes.Count;

        var storage = await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Nor, TestContext.Current.CancellationToken);

        Assert.NotNull(storage);
        Assert.Equal(0x040003u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[before + 3]));
        Assert.Equal(0x040007u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[before + 7]));
        Assert.Equal(MtkXFlashStorageKind.Nor, storage.Kind);
        Assert.Equal(pageSize, storage.BlockSize);
        Assert.Empty(storage.CidHex);
        var region = Assert.Single(storage.Regions);
        Assert.Equal(8u, region.PartitionId);
        Assert.Equal(1024 * 1024, region.CapacityBytes);
        Assert.False(region.RequiresAuthenticatedAccess);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("zero-page")]
    [InlineData("non-power-page")]
    [InlineData("huge-page")]
    [InlineData("empty")]
    [InlineData("overflow")]
    [InlineData("unaligned")]
    [InlineData("ambiguous-packet")]
    [InlineData("unaligned-packet")]
    public async Task DiscoverXFlashStorageAsync_InvalidNorGeometry_FaultsWithoutPublishing(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var geometry = NorGeometry(1);
        var packets = NorPacketLengths(512);
        switch (failure)
        {
            case "truncated": geometry = geometry[..15]; break;
            case "zero-page": BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 0); break;
            case "non-power-page": BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 3); break;
            case "huge-page": BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 131072); break;
            case "empty": BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), 0); break;
            case "overflow": BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), ulong.MaxValue); break;
            case "unaligned": BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), 511); break;
            case "ambiguous-packet": packets = NorPacketLengths(4); break;
            case "unaligned-packet": packets = NorPacketLengths(513); break;
        }
        EnqueueControlReply(transport, geometry);
        EnqueueControlReply(transport, packets);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.DiscoverXFlashStorageAsync(
            MtkXFlashStorageKind.Nor, TestContext.Current.CancellationToken));

        Assert.Null(session.XFlashStorage);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UnifiedBootstrapAsync_NorProfile_CompletesNorDiscoveryWithoutStorageWrites()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var options = BootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nor };
        EnqueueBootstrap(transport, false, geometry: NorGeometry());

        using var session = await MtkBootstrap.InitializeAsync(
            transport, MtkDaProtocol.XFlash, options, TestContext.Current.CancellationToken);

        Assert.Equal(MtkXFlashStorageKind.Nor, session.XFlashStorage!.Kind);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.Equal(0, transport.DisposeCount);
    }

    [Fact]
    public async Task UnifiedBootstrapAsync_NorOnXmlProtocol_RejectsBeforeOpeningSerial()
    {
        using var dir = new TempDirectory();
        var options = XmlBootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nor };
        var opens = 0;
        using var transport = new ScriptedCommTransport();

        await Assert.ThrowsAsync<ArgumentException>(() => MtkBootstrap.ConnectSerialCoreAsync(
            ExplicitSerial(), MtkDaProtocol.Xml, options, (_, _) =>
            {
                opens++;
                return Task.FromResult<ICommTransport>(transport);
            }, TestContext.Current.CancellationToken));

        Assert.Equal(0, opens);
        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData(1, 0L, 1L)]
    [InlineData(1, 3L, 2L)]
    [InlineData(1, 7L, 4L)]
    [InlineData(4, 4L, 4L)]
    [InlineData(1, 1048575L, 1L)]
    [InlineData(256, 256L, 768L)]
    public async Task ReadXFlashStorageAsync_NorRange_UsesExactWindowWithoutExposingAdjacentBytes(
        int pageSize, long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport, pageSize);
        var wireOffset = offset & ~7L;
        var wireEnd = (offset + length + 7) & ~7L;
        var bytes = Enumerable.Range(0, (int)(wireEnd - wireOffset)).Select(index => (byte)(index + 1)).ToArray();
        EnqueueTransferStart(transport, read: true);
        for (var index = 0; index < bytes.Length; index += 512)
        {
            EnqueueXFlashFrame(transport, bytes[index..Math.Min(index + 512, bytes.Length)]);
        }
        EnqueueXFlashValue(transport, 0);
        using var output = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.ReadXFlashStorageAsync(8, offset, length, output, progress, TestContext.Current.CancellationToken);

        Assert.Equal(bytes.AsSpan((int)(offset - wireOffset), (int)length).ToArray(), output.ToArray());
        Assert.True(output.CanWrite);
        Assert.Equal(length, progress.Values.Last());
        AssertNorParameters(transport.Writes[before + 3], wireOffset, wireEnd - wireOffset);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(256, 768)]
    public async Task WriteXFlashStorageAsync_NorRange_SendsExactBytesWithoutPadding(int pageSize, int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport, pageSize);
        EnqueueTransferStart(transport, read: false);
        var packets = (length + 511) / 512;
        for (var index = 0; index < packets + 1; index++)
        {
            EnqueueXFlashValue(transport, 0);
        }
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index + 3)).ToArray();
        using var input = new FragmentedSource(bytes);
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await session.WriteXFlashStorageAsync(8, pageSize, length, input, progress, TestContext.Current.CancellationToken);

        AssertNorParameters(transport.Writes[before + 3], pageSize, length);
        for (var index = 0; index < packets; index++)
        {
            var expected = bytes[(index * 512)..Math.Min((index + 1) * 512, length)];
            Assert.Equal(expected, transport.Writes[before + 9 + index * 6]);
            Assert.Equal((uint)(expected.Sum(value => (int)value) & 0xFFFF),
                BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[before + 7 + index * 6]));
        }
        Assert.Equal(length, progress.Values.Last());
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task EraseXFlashNorStorageAsync_ExplicitEraseGeometry_RequiresFinalCompletion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040004);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0x40040005);
        var before = transport.Writes.Count;

        await session.EraseXFlashNorStorageAsync(4096, 8192, 4096, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0x010003u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[before + 1]));
        AssertNorParameters(transport.Writes[before + 3], 4096, 8192);
        Assert.Equal(before + 6, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(0, 4096L, 4096L)]
    [InlineData(3, 4096L, 4096L)]
    [InlineData(128, 0L, 4096L)]
    [InlineData(4096, 256L, 4096L)]
    [InlineData(4096, 0L, 256L)]
    [InlineData(4096, 1048576L, 4096L)]
    [InlineData(33554432, 0L, 4096L)]
    public async Task EraseXFlashNorStorageAsync_InvalidGeometry_RejectsBeforeIo(int block, long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseXFlashNorStorageAsync(
            offset, length, block, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task NorStorageAsync_BlockDeviceOperations_RejectWithoutIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashGptAsync(
            8, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.EraseXFlashStorageAsync(
            8, 0, 4096, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("truncated")]
    [InlineData("cancel")]
    public async Task ReadXFlashStorageAsync_NorWindowFailure_FaultsWithoutReportingCompletion(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport, 1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        EnqueueTransferStart(transport, read: true);
        EnqueueXFlashFrame(transport, [1, 2, 3, 4, 5, 6, 7, 8]);
        if (failure == "status")
        {
            EnqueueXFlashValue(transport, 0xC0070004);
        }
        if (failure == "cancel")
        {
            var writes = 0;
            transport.OnWrite = _ =>
            {
                if (++writes == 6)
                {
                    cancellation.Cancel();
                }
                return [];
            };
        }
        using var output = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        if (failure == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashStorageAsync(
                8, 3, 2, output, progress, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashStorageAsync(
                8, 3, 2, output, progress, cancellation.Token));
        }

        Assert.Equal(new byte[] { 4, 5 }, output.ToArray());
        Assert.Empty(progress.Values);
        Assert.True(output.CanWrite);
        Assert.Equal(before + 6, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseXFlashNorStorageAsync_UnconfirmedCompletion_FaultsWithoutRetry(bool timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, timeout ? 0x40040004u : 0u);
        if (timeout)
        {
            EnqueueXFlashValue(transport, 60000);
        }
        var before = transport.Writes.Count;

        if (timeout)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => session.EraseXFlashNorStorageAsync(
                0, 4096, 4096, 100, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXFlashNorStorageAsync(
                0, 4096, 4096, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashNorStorageAsync(
            0, 4096, 4096, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before + 4, transport.Writes.Count);
    }

    [Theory]
    [InlineData(1u, 0L, 256L)]
    [InlineData(8u, 1L, 256L)]
    [InlineData(8u, 0L, 1L)]
    [InlineData(8u, 1048576L, 256L)]
    [InlineData(8u, long.MaxValue, 256L)]
    public async Task NorStorageAsync_InvalidRange_RejectsReadAndWriteBeforeIo(uint region, long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNorSessionAsync(transport);
        using var stream = new MemoryStream(new byte[256]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashStorageAsync(
            region, offset, length, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXFlashStorageAsync(
            region, offset, length, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task EraseXFlashNorStorageAsync_EmmcSession_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashNorStorageAsync(
            0, 512, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    private static byte[] NorGeometry(int pageSize = 256)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)pageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 1024 * 1024);
        return bytes;
    }

    private static byte[] NorPacketLengths(int packetSize)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)packetSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)packetSize);
        return bytes;
    }

    private static async Task<MtkSession> CreateNorSessionAsync(ScriptedCommTransport transport, int pageSize = 256)
    {
        var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, NorGeometry(pageSize));
        EnqueueControlReply(transport, NorPacketLengths(512));
        await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Nor, TestContext.Current.CancellationToken);
        return session;
    }

    private static void AssertNorParameters(byte[] parameters, long offset, long length)
    {
        Assert.Equal(56, parameters.Length);
        Assert.Equal(0x20u, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(4)));
        Assert.Equal((ulong)offset, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
        Assert.Equal((ulong)length, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(16)));
        Assert.All(parameters.Skip(24), value => Assert.Equal(0, value));
    }
}