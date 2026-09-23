using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public class FirehoseSparseTests
{
    private const ushort Raw = 0xCAC1;
    private const ushort Fill = 0xCAC2;
    private const ushort DontCare = 0xCAC3;
    private const ushort Checksum = 0xCAC4;
    private const string Ready = "<data><response value='ACK' rawmode='true'/></data>";
    private const string Complete = "<data><response value='ACK' rawmode='false'/></data>";

    [Fact]
    public async Task ProgramSparseAsync_DeclaredLengthExceedsRange_RejectsAtHeaderWithoutFaulting()
    {
        using var image = new MemoryStream(BuildImage((DontCare, 2, []))[..28]);
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            protocol.ProgramSparseAsync(image, "0", maximumSectors: 1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("logical length", error.Message, StringComparison.Ordinal);
        Assert.Equal(28, image.Position);
        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
        Assert.True(image.CanRead);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(long.MaxValue)]
    public void Read_ValidImageWithinLargeSectorLimit_DoesNotOverflow(long maximumSectors)
    {
        using var stream = new MemoryStream(BuildImage((DontCare, 2, [])));

        using var image = FirehoseSparseLayout.Read(stream, 512, FirehoseSectorAddress.Parse("0"),
            maximumSectors, TestContext.Current.CancellationToken);

        Assert.Single(image.Chunks);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("100", false)]
    [InlineData("NUM_DISK_SECTORS-6.", true)]
    public async Task ProgramSparseAsync_MixedChunks_PreservesSkippedSectorsAndWritesExactData(string address, bool relative)
    {
        var raw = Enumerable.Repeat((byte)0x61, 1024).ToArray();
        byte[] pattern = [0x12, 0x34, 0x56, 0x78];
        var tail = Enumerable.Repeat((byte)0x42, 512).ToArray();
        using var image = new MemoryStream(BuildImage((DontCare, 1, []), (Raw, 2, raw),
            (Fill, 1, pattern), (DontCare, 1, []), (Raw, 1, tail)));
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        var storage = Enumerable.Repeat((byte)0xA5, 6 * 512).ToArray();
        var starts = new List<string>();
        var remaining = 0;
        var position = 0;
        transport.OnWrite = bytes =>
        {
            if (remaining == 0)
            {
                var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Element("program")!;
                var start = (string)command.Attribute("start_sector")!;
                starts.Add(start);
                var sector = relative
                    ? 6 - int.Parse(start["NUM_DISK_SECTORS-".Length..^1])
                    : int.Parse(start) - 100;
                position = sector * 512;
                remaining = (int)command.Attribute("num_partition_sectors")! * 512;
                return [Encoding.UTF8.GetBytes(Ready)];
            }

            Assert.InRange(bytes.Length, 1, Math.Min(remaining, 512));
            bytes.CopyTo(storage, position);
            position += bytes.Length;
            remaining -= bytes.Length;
            return remaining == 0 ? [Encoding.UTF8.GetBytes(Complete)] : [];
        };

        var response = await protocol.ProgramSparseAsync(image, address, maximumSectors: 6,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(relative ? ["NUM_DISK_SECTORS-5.", "NUM_DISK_SECTORS-3.", "NUM_DISK_SECTORS-1."] : new[] { "101", "103", "105" }, starts);
        Assert.All(storage[..512], value => Assert.Equal((byte)0xA5, value));
        Assert.Equal(raw, storage[512..1536]);
        Assert.Equal(Enumerable.Range(0, 512).Select(i => pattern[i % 4]), storage[1536..2048]);
        Assert.All(storage[2048..2560], value => Assert.Equal((byte)0xA5, value));
        Assert.Equal(tail, storage[2560..]);
        Assert.True(image.CanRead);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("file-header")]
    [InlineData("chunk-header")]
    [InlineData("block-size")]
    [InlineData("total-blocks")]
    [InlineData("total-chunks")]
    [InlineData("image-checksum")]
    [InlineData("unknown-chunk")]
    [InlineData("raw-length")]
    [InlineData("late-fill-length")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("crc")]
    public async Task ProgramSparseAsync_MalformedImage_RejectsBeforeAnyDeviceWrite(string corruption)
    {
        var bytes = BuildImage((Raw, 1, new byte[512]), (Fill, 1, [1, 2, 3, 4]));
        switch (corruption)
        {
            case "magic": bytes[0] = 0; break;
            case "version": bytes[4] = 2; break;
            case "file-header": bytes[8] = 27; break;
            case "chunk-header": bytes[10] = 11; break;
            case "block-size": bytes[12] = 1; break;
            case "total-blocks": bytes[16] = 3; break;
            case "total-chunks": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), uint.MaxValue); break;
            case "image-checksum": bytes[24] = 1; break;
            case "unknown-chunk": bytes[28] = 0; break;
            case "raw-length": bytes[36] = 0; break;
            case "late-fill-length": bytes[560] = 13; break;
            case "truncated": bytes = bytes[..^1]; break;
            case "trailing": bytes = [.. bytes, 0]; break;
            case "crc": bytes = BuildImage((Raw, 1, new byte[512]), (Checksum, 0, [1, 0, 0, 0])); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        using var image = new MemoryStream(bytes);
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            protocol.ProgramSparseAsync(image, 0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("NUM_DISK_SECTORS-1.", 2)]
    public async Task ProgramSparseAsync_ImageExceedsRange_RejectsBeforeWriting(string address, long sectors)
    {
        using var image = new MemoryStream(BuildImage((Raw, 1, new byte[512]), (DontCare, 1, [])));
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);

        if (address == "0")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                protocol.ProgramSparseAsync(image, address, maximumSectors: sectors, cancellationToken: TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                protocol.ProgramSparseAsync(image, address, maximumSectors: sectors, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramSparseAsync_AllSkipped_UsesActualProbeWithoutWritingStorage()
    {
        using var image = new MemoryStream(BuildImage((DontCare, 2, [])));
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value='NAK'/></data>");
        using var protocol = CreateProtocol(transport);

        var response = await protocol.ProgramSparseAsync(image, 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsNak);
        Assert.Contains("<nop ", Encoding.UTF8.GetString(Assert.Single(transport.Writes)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramSparseAsync_CorrectChecksums_WritesPayload()
    {
        var payload = Enumerable.Repeat((byte)0x52, 512).ToArray();
        var checksum = Crc32.HashToUInt32(payload);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, checksum);
        var bytes = BuildImage((Raw, 1, payload), (Checksum, 0, crcBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), checksum);
        using var image = new MemoryStream(bytes);
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count == 1 ? Ready : Complete)];
        using var protocol = CreateProtocol(transport);

        await protocol.ProgramSparseAsync(image, 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(payload, transport.Writes[1]);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task ProgramSparseAsync_IndividualChunkNotSectorAligned_RejectsBeforeWriting()
    {
        var bytes = BuildImage((Raw, 1, new byte[256]), (Fill, 1, [1, 2, 3, 4]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 256);
        using var image = new MemoryStream(bytes);
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            protocol.ProgramSparseAsync(image, 0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramSparseAsync_SourceTruncatedAfterPreflight_FaultsWithoutSendingPaddedData()
    {
        using var image = new MemoryStream(BuildImage((Raw, 1, new byte[512])));
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        transport.OnWrite = _ =>
        {
            image.SetLength(40);
            return [Encoding.UTF8.GetBytes(Ready)];
        };

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            protocol.ProgramSparseAsync(image, 0, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramSparseAsync_CancelledDuringPreflight_PropagatesWithoutFaultingSession()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var image = new CancelOnFillReadStream(BuildImage((Fill, 128, [1, 2, 3, 4])), cancellation);
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            protocol.ProgramSparseAsync(image, 0, cancellationToken: cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.True(image.CanRead);
        Assert.False(protocol.RequiresReconnect);
    }

    private sealed class CancelOnFillReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            if (Position == Length)
            {
                cancellation.Cancel();
            }
            return count;
        }
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport) => new(transport, new FirehoseConfiguration
    {
        SectorSize = 512,
        MaxPayloadSizeToTarget = 512,
        AutoDetectSectorSize = false
    });

    internal static byte[] BuildImage(params (ushort Type, uint Blocks, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xED26FF3Au);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)28);
        writer.Write((ushort)12);
        writer.Write(512u);
        writer.Write(checked((uint)chunks.Sum(c => (long)c.Blocks)));
        writer.Write((uint)chunks.Length);
        writer.Write(0u);
        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Type);
            writer.Write((ushort)0);
            writer.Write(chunk.Blocks);
            writer.Write(checked((uint)(12 + chunk.Data.Length)));
            writer.Write(chunk.Data);
        }
        writer.Flush();
        return stream.ToArray();
    }
}