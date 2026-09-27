using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    public static IEnumerable<object[]> NandTransfers()
    {
        foreach (var mode in Enum.GetValues<MtkXFlashNandTransferMode>())
        {
            foreach (var unit in Enum.GetValues<MtkXFlashNandLengthUnit>())
            {
                yield return [mode, unit, false];
                yield return [mode, unit, true];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NandTransfers))]
    public async Task NandTransferAsync_ExplicitPageStream_UsesSelectedWireParametersAndExactBytes(
        MtkXFlashNandTransferMode mode, MtkXFlashNandLengthUnit unit, bool write)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        var options = NandOptions(mode, unit);
        var length = (int)options.GetTransferLength(session.XFlashStorage!.Nand!, 6144);
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index * 7 + 3)).ToArray();
        var recordSize = length / 3;
        var packetSize = write ? 4096 / recordSize * recordSize : 4096;
        EnqueueTransferStart(transport, read: !write);
        for (var index = 0; index < length; index += packetSize)
        {
            EnqueueXFlashFrame(transport, write ? new byte[4] : bytes[index..Math.Min(index + packetSize, length)]);
        }
        EnqueueXFlashValue(transport, 0);
        using var source = new FragmentedSource(bytes);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        if (write)
        {
            await session.WriteXFlashNandStorageAsync(2048, 6144, options, source, progress, TestContext.Current.CancellationToken);
        }
        else
        {
            await session.ReadXFlashNandStorageAsync(2048, 6144, options, destination, progress, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, destination.ToArray());
        }

        var frames = NandWrittenFrames(transport, before);
        Assert.Equal(write ? 0x010004u : 0x010005u, BinaryPrimitives.ReadUInt32LittleEndian(frames[0]));
        var parameters = frames[1];
        Assert.Equal(56, parameters.Length);
        Assert.Equal(0x10u, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(4)));
        Assert.Equal(2048ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
        Assert.Equal((ulong)(unit == MtkXFlashNandLengthUnit.MainAreaBytes ? 6144 : length),
            BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(16)));
        Assert.All(parameters.Skip(24).Take(16), value => Assert.Equal(0, value));
        Assert.Equal((uint)mode, BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(40)));
        Assert.All(parameters.Skip(44), value => Assert.Equal(0, value));
        if (write)
        {
            for (var index = 0; index < length; index += packetSize)
            {
                var frameIndex = 2 + index / packetSize * 3;
                var expected = bytes[index..Math.Min(index + packetSize, length)];
                Assert.Equal(new byte[4], frames[frameIndex]);
                Assert.Equal((uint)(expected.Sum(value => (int)value) & 0xFFFF),
                    BinaryPrimitives.ReadUInt32LittleEndian(frames[frameIndex + 1]));
                Assert.Equal(expected, frames[frameIndex + 2]);
                Assert.Equal(0, frames[frameIndex + 2].Length % recordSize);
            }
        }
        Assert.Equal(length, progress.Values.Last());
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(MtkXFlashNandAddressType.Physical)]
    [InlineData(MtkXFlashNandAddressType.PhysicalPmt)]
    public async Task ReadXFlashNandStorageAsync_ExplicitPhysicalAddress_PreservesExtensionAndUsesTotalCapacity(
        MtkXFlashNandAddressType address)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport, available: 0);
        var options = NandOptions() with
        {
            Policy = new MtkXFlashNandPolicy
            {
                AddressType = address,
                CellUsage = MtkXFlashNandCellUsage.MultiLevel,
                StorageType = MtkXFlashNandStorageType.Spi,
                Region = 7,
                SystemSlcPercent = 11,
                UserSlcPercent = 22,
                PhysicalMaximumSizeKiB = 128
            }
        };
        EnqueueTransferStart(transport, read: true);
        EnqueueXFlashFrame(transport, new byte[2048]);
        EnqueueXFlashValue(transport, 0);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await session.ReadXFlashNandStorageAsync(1046528, 2048, options, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        var parameters = NandWrittenFrames(transport, before)[1];
        Assert.Equal(0x15u, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(1046528ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
        Assert.Equal(new uint[] { 1, (uint)address, 0, 7, 1, 11, 22, 128 },
            Enumerable.Range(0, 8).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(24 + index * 4))));
    }

    [Theory]
    [InlineData("logical-capacity")]
    [InlineData("physical-capacity")]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("mode")]
    [InlineData("length-unit")]
    [InlineData("address-type")]
    [InlineData("cell-usage")]
    [InlineData("storage-type")]
    [InlineData("percentage")]
    [InlineData("physical-limit")]
    [InlineData("missing-spare")]
    public async Task NandTransferAsync_InvalidPolicy_RejectsBeforeIoAndPreservesSession(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport, spareSize: failure == "missing-spare" ? 0 : 64);
        var options = NandOptions();
        long offset = 0;
        long length = 2048;
        switch (failure)
        {
            case "logical-capacity": offset = 786432; break;
            case "physical-capacity": offset = 1048576; options = options with { Policy = options.Policy with { AddressType = MtkXFlashNandAddressType.Physical } }; break;
            case "offset": offset = 1; break;
            case "length": length = 2049; break;
            case "mode": options = options with { Mode = (MtkXFlashNandTransferMode)5 }; break;
            case "length-unit": options = options with { LengthUnit = (MtkXFlashNandLengthUnit)9 }; break;
            case "address-type": options = options with { Policy = options.Policy with { AddressType = (MtkXFlashNandAddressType)9 } }; break;
            case "cell-usage": options = options with { Policy = options.Policy with { CellUsage = (MtkXFlashNandCellUsage)9 } }; break;
            case "storage-type": options = options with { Policy = options.Policy with { StorageType = (MtkXFlashNandStorageType)1 } }; break;
            case "percentage": options = options with { Policy = options.Policy with { UserSlcPercent = 101 } }; break;
            case "physical-limit": options = options with { Policy = options.Policy with { PhysicalMaximumSizeKiB = uint.MaxValue } }; break;
            case "missing-spare": options = options with { Mode = MtkXFlashNandTransferMode.PageAndSpare }; break;
        }
        using var stream = new MemoryStream(new byte[4096]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashNandStorageAsync(
            offset, length, options, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXFlashNandStorageAsync(
            offset, length, options, stream, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashNandStorageAsync_RecordExceedsPacket_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport, packetLength: 2048);
        using var source = new MemoryStream(new byte[2112]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXFlashNandStorageAsync(
            0, 2048, NandOptions(MtkXFlashNandTransferMode.PageAndSpare), source,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteXFlashNandStorageAsync_ShortPageStream_DoesNotPadOrAcknowledgePartialRecord(bool seekable)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        var options = NandOptions(MtkXFlashNandTransferMode.PageAndSpare);
        using MemoryStream source = seekable ? new MemoryStream(new byte[2200]) : new NonSeekableStorageSource(new byte[2200]);
        if (!seekable)
        {
            EnqueueTransferStart(transport, read: false);
            EnqueueXFlashValue(transport, 0);
        }
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        if (seekable)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.WriteXFlashNandStorageAsync(
                0, 4096, options, source, progress, TestContext.Current.CancellationToken));
            Assert.Equal(before, transport.Writes.Count);
            Assert.Empty(progress.Values);
            Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        }
        else
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteXFlashNandStorageAsync(
                0, 4096, options, source, progress, TestContext.Current.CancellationToken));
            Assert.Equal(5, NandWrittenFrames(transport, before).Count);
            Assert.Equal(new long[] { 2112 }, progress.Values);
            Assert.Equal(MtkSessionState.Faulted, session.State);
        }
    }

    private static List<byte[]> NandWrittenFrames(ScriptedCommTransport transport, int start)
    {
        var bytes = transport.Writes.Skip(start).SelectMany(value => value).ToArray();
        var frames = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length;)
        {
            Assert.True(bytes.Length - offset >= 12);
            Assert.Equal(0xFEEEEEEFu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8)));
            Assert.InRange(length, 0, bytes.Length - offset - 12);
            frames.Add(bytes.AsSpan(offset + 12, length).ToArray());
            offset += 12 + length;
        }
        return frames;
    }
}