using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class FirehoseSkipblockTests
{
    private const int SectorSize = 512;
    private const int BootFirstLba = 40;

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_MatchedRegion_SkipsProgramCommand()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(SectorSize);
        Array.Copy(payload, 0, disk, BootFirstLba * SectorSize, payload.Length);
        using var transport = DiskTransport(disk);
        using var protocol = Protocol(transport, slot: 2);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            skipMatchedRegions: true, TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(SectorSize, SectorSize), result);
        var commands = Commands(transport).ToArray();
        Assert.DoesNotContain(commands, command => command.Name.LocalName == "program");
        var lastDigest = Array.FindLastIndex(commands, command => command.Name.LocalName == "getsha256digest");
        Assert.DoesNotContain(commands[(lastDigest + 1)..], command => command.Name.LocalName == "read");
        var digests = commands.Where(command => command.Name.LocalName == "getsha256digest").ToArray();
        Assert.Equal(2, digests.Length);
        Assert.Equal("2", digests[0].Attribute("slot")?.Value);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_MismatchedRegion_ProgramsChunkWithoutReadback()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(SectorSize);
        disk.AsSpan(BootFirstLba * SectorSize, SectorSize).Fill(0xA5);
        using var transport = DiskTransport(disk);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            skipMatchedRegions: true, TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(SectorSize, SectorSize), result);
        Assert.Equal(payload, disk[(BootFirstLba * SectorSize)..((BootFirstLba + 1) * SectorSize)]);
        var commands = Commands(transport).ToArray();
        var programs = commands.Where(command => command.Name.LocalName == "program").ToArray();
        Assert.Single(programs);
        Assert.Equal(BootFirstLba, (long)programs[0].Attribute("start_sector")!);
        Assert.Equal(1, (long)programs[0].Attribute("num_partition_sectors")!);
        var programIndex = Array.FindIndex(commands, command => command.Name.LocalName == "program");
        Assert.DoesNotContain(commands[(programIndex + 1)..], command => command.Name.LocalName == "read");
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task TryProgramSkipblockOwnedAsync_ShortSourceZeroPadded_MatchesAndSkips()
    {
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        var disk = GptReaderTests.CreateDisk(SectorSize);
        Array.Copy(payload, 0, disk, BootFirstLba * SectorSize, payload.Length);
        using var transport = DiskTransport(disk);
        using var protocol = Protocol(transport);
        using var source = new MemoryStream(payload);

        var handled = await protocol.TryProgramSkipblockOwnedAsync(source, BootFirstLba, 4, "partition.bin", 0,
            TestContext.Current.CancellationToken);

        Assert.True(handled);
        var commands = Commands(transport).ToArray();
        Assert.DoesNotContain(commands, command => command.Name.LocalName == "program");
        var digests = commands.Where(command => command.Name.LocalName == "getsha256digest").ToArray();
        Assert.Single(digests);
        Assert.Equal(BootFirstLba, (long)digests[0].Attribute("start_sector")!);
        Assert.Equal(4, (long)digests[0].Attribute("num_partition_sectors")!);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_DigestRejected_FallsBackToFullProgram()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(SectorSize);
        using var transport = DiskTransport(disk, rejectDigest: true);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            skipMatchedRegions: true, TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(SectorSize, SectorSize), result);
        Assert.Equal(payload, disk[(BootFirstLba * SectorSize)..((BootFirstLba + 1) * SectorSize)]);
        var commands = Commands(transport).ToArray();
        var programs = commands.Where(command => command.Name.LocalName == "program").ToArray();
        Assert.Single(programs);
        Assert.Equal(BootFirstLba, (long)programs[0].Attribute("start_sector")!);
        Assert.Equal(1, (long)programs[0].Attribute("num_partition_sectors")!);
        Assert.Contains(commands, command => command.Name.LocalName == "read");
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task TryProgramSkipblockOwnedAsync_MultiChunkRegion_SkipsMatchAndProgramsMismatch()
    {
        const long chunkSectors = 512L * 1024 * 1024 / SectorSize;
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        using var transport = new ScriptedCommTransport();
        var payloadWrites = new List<byte[]>();
        transport.OnWrite = bytes =>
        {
            if (bytes[0] != (byte)'<')
            {
                payloadWrites.Add(bytes.ToArray());
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
            }

            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name.LocalName == "getsha256digest")
            {
                var digest = VirtualFlashDigest((long)command.Attribute("start_sector")!,
                    (long)command.Attribute("num_partition_sectors")!);
                return [Encoding.UTF8.GetBytes(
                    $"<data><log value='Digest {Convert.ToHexString(digest).ToLowerInvariant()}'/><response value='ACK' rawmode='false'/></data>")];
            }

            if (command.Name.LocalName != "program")
            {
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
            }

            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>")];
        };
        using var protocol = Protocol(transport);
        using var source = new MemoryStream(payload);

        var handled = await protocol.TryProgramSkipblockOwnedAsync(source, 0, chunkSectors + 1, "partition.bin", 0,
            TestContext.Current.CancellationToken);

        Assert.True(handled);
        var commands = Commands(transport).ToArray();
        var digests = commands.Where(command => command.Name.LocalName == "getsha256digest").ToArray();
        Assert.Equal(2, digests.Length);
        Assert.Equal(0, (long)digests[0].Attribute("start_sector")!);
        Assert.Equal(chunkSectors, (long)digests[1].Attribute("start_sector")!);
        var programs = commands.Where(command => command.Name.LocalName == "program").ToArray();
        Assert.Single(programs);
        Assert.Equal(chunkSectors, (long)programs[0].Attribute("start_sector")!);
        Assert.Equal(1, (long)programs[0].Attribute("num_partition_sectors")!);
        var payloadWrite = Assert.Single(payloadWrites);
        Assert.Equal(new byte[SectorSize], payloadWrite);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_SkipblockOptOut_QueriesDigestOnlyAfterProgram()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(SectorSize);
        Array.Copy(payload, 0, disk, BootFirstLba * SectorSize, payload.Length);
        using var transport = DiskTransport(disk);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(SectorSize, SectorSize), result);
        var commands = Commands(transport).ToArray();
        var programIndex = Array.FindIndex(commands, command => command.Name.LocalName == "program");
        Assert.True(programIndex >= 0);
        Assert.DoesNotContain(commands.Take(programIndex), command => command.Name.LocalName == "getsha256digest");
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_NandStorage_NeverQueriesDigest()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, SectorSize).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(SectorSize);
        using var transport = DiskTransport(disk);
        using var protocol = Protocol(transport, storageType: FirehoseStorageType.Nand);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            skipMatchedRegions: true, TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(SectorSize, SectorSize), result);
        var commands = Commands(transport).ToArray();
        Assert.DoesNotContain(commands, command => command.Name.LocalName == "getsha256digest");
        Assert.Contains(commands, command => command.Name.LocalName == "read");
    }

    [Fact]
    public void BuildGetSha256Digest_WithSlot_EmitsSlotAttribute()
    {
        var xml = FirehoseXmlBuilder.BuildGetSha256Digest(SectorSize, 8, 0, BootFirstLba, 2);

        Assert.Contains("slot=\"2\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGetSha256Digest_WithoutSlot_OmitsSlotAttribute()
    {
        var xml = FirehoseXmlBuilder.BuildGetSha256Digest(SectorSize, 8, 0, BootFirstLba);

        Assert.DoesNotContain("slot=", xml, StringComparison.Ordinal);
    }

    private static FirehoseProtocol Protocol(ScriptedCommTransport transport, int slot = 0,
        FirehoseStorageType storageType = FirehoseStorageType.Emmc) => new(transport,
        new FirehoseConfiguration
        {
            SectorSize = SectorSize,
            AutoDetectSectorSize = false,
            MaxPayloadSizeToTarget = 65536,
            Slot = slot,
            StorageType = storageType
        });

    private static XElement[] Commands(ScriptedCommTransport transport) => transport.Writes
        .Where(bytes => bytes[0] == '<')
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single())
        .ToArray();

    private static byte[] VirtualFlashDigest(long startSector, long numSectors)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        var position = startSector * SectorSize;
        var end = position + numSectors * SectorSize;
        while (position < end)
        {
            var want = (int)Math.Min(buffer.Length, end - position);
            FillVirtualFlash(buffer.AsSpan(0, want), position);
            hasher.AppendData(buffer, 0, want);
            position += want;
        }

        return hasher.GetHashAndReset();
    }

    private static void FillVirtualFlash(Span<byte> span, long start)
    {
        const long mismatchOffset = 512L * 1024 * 1024;
        span.Clear();
        FillRange(span, start, 0, SectorSize, 0x42);
        FillRange(span, start, mismatchOffset, mismatchOffset + SectorSize, 0xA5);
    }

    private static void FillRange(Span<byte> span, long spanStart, long rangeStart, long rangeEnd, byte value)
    {
        var from = Math.Max(spanStart, rangeStart);
        var to = Math.Min(spanStart + span.Length, rangeEnd);
        if (from < to)
        {
            span[(int)(from - spanStart)..(int)(to - spanStart)].Fill(value);
        }
    }

    private static ScriptedCommTransport DiskTransport(byte[] disk, bool rejectDigest = false)
    {
        var transport = new ScriptedCommTransport();
        var remaining = 0;
        var position = 0;
        transport.OnWrite = bytes =>
        {
            if (remaining > 0)
            {
                bytes.CopyTo(disk, position);
                position += bytes.Length;
                remaining -= bytes.Length;
                if (remaining != 0)
                {
                    return [];
                }

                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
            }

            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name.LocalName == "program")
            {
                position = checked((int)command.Attribute("start_sector")! * SectorSize);
                remaining = checked((int)command.Attribute("num_partition_sectors")! * SectorSize);
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>")];
            }

            if (command.Name.LocalName == "getsha256digest")
            {
                if (rejectDigest)
                {
                    return [Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>")];
                }

                var start = checked((int)command.Attribute("start_sector")! * SectorSize);
                var count = checked((int)command.Attribute("num_partition_sectors")! * SectorSize);
                var digest = SHA256.HashData(disk[start..(start + count)]);
                return [Encoding.UTF8.GetBytes(
                    $"<data><log value='Digest {Convert.ToHexString(digest).ToLowerInvariant()}'/><response value='ACK' rawmode='false'/></data>")];
            }

            if (command.Name.LocalName == "getstorageinfo")
            {
                return [Encoding.UTF8.GetBytes(
                    $"<data><log value='{{\"storage_info\":{{\"block_size\":{SectorSize},\"total_blocks\":128}}}}'/><response value='ACK'/></data>")];
            }

            if (command.Name.LocalName == "read")
            {
                var offset = checked((int)command.Attribute("start_sector")! * SectorSize);
                var count = checked((int)command.Attribute("num_partition_sectors")! * SectorSize);
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                    disk.AsSpan(offset, count).ToArray(),
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")
                ];
            }

            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        return transport;
    }
}