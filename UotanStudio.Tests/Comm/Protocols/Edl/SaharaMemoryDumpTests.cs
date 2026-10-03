using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Sahara memory-debug (RAM dump) host side: table discovery, bounded chunked region reads, file
/// publication, output-name safety and reset — scripted through
/// <see cref="ScriptedCommTransport"/> (no real hardware).
/// <para>Sahara 内存调试（RAM dump）主机侧：区域表发现、有界分块的区域读取、文件发布、输出文件名安全
/// 与复位——全部通过 <see cref="ScriptedCommTransport"/> 脚本化模拟（无真实硬件）。</para>
/// </summary>
public class SaharaMemoryDumpTests
{
    private const int ChunkBytes = 1024 * 1024;
    private const ulong TableAddress = 0x8F00_0000UL;

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_MemoryDebug64Table_ParsesEveryRecord()
    {
        var table = Concat(
            BuildRecord64(1, 0x1000, 0x40, "Secure Boot Loader", "sbl.bin"),
            BuildRecord64(2, 0x2000, 0, "Empty segment", "empty.bin"));
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloMemoryDebug());
        transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
        transport.Enqueue(table);
        var sahara = new SaharaProtocol(transport);

        var regions = await sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, regions.Count);
        Assert.Equal(0x1000UL, regions[0].Address);
        Assert.Equal(0x40UL, regions[0].Length);
        Assert.Equal(1UL, regions[0].SavePreference);
        Assert.Equal("Secure Boot Loader", regions[0].Description);
        Assert.Equal("sbl.bin", regions[0].FileName);
        Assert.Equal(0x2000UL, regions[1].Address);
        Assert.Equal(0UL, regions[1].Length);
        Assert.Equal("empty.bin", regions[1].FileName);

        Assert.Equal(2, transport.Writes.Count);
        var helloResponse = transport.Writes[0];
        Assert.Equal(0x02u, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse));
        Assert.Equal(48u, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(0x14)));

        var tableRead = transport.Writes[1];
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(tableRead));
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(tableRead.AsSpan(4)));
        Assert.Equal(TableAddress, BinaryPrimitives.ReadUInt64LittleEndian(tableRead.AsSpan(8)));
        Assert.Equal((ulong)table.Length, BinaryPrimitives.ReadUInt64LittleEndian(tableRead.AsSpan(16)));
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_LegacyTable_ParsesFiftyTwoByteRecords()
    {
        var table = BuildRecord32(3, 0x3000, 0x80, "Modem debug", "mdlog.bin");
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloMemoryDebug());
        transport.Enqueue(BuildMemoryDebugPacket(0x2000, (uint)table.Length));
        transport.Enqueue(table);
        var sahara = new SaharaProtocol(transport);

        var regions = await sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken);

        Assert.Single(regions);
        Assert.Equal(0x3000UL, regions[0].Address);
        Assert.Equal(0x80UL, regions[0].Length);
        Assert.Equal(3UL, regions[0].SavePreference);
        Assert.Equal("Modem debug", regions[0].Description);
        Assert.Equal("mdlog.bin", regions[0].FileName);

        var tableRead = transport.Writes[1];
        Assert.Equal(0x0Au, BinaryPrimitives.ReadUInt32LittleEndian(tableRead));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(tableRead.AsSpan(4)));
        Assert.Equal(0x2000u, BinaryPrimitives.ReadUInt32LittleEndian(tableRead.AsSpan(8)));
        Assert.Equal((uint)table.Length, BinaryPrimitives.ReadUInt32LittleEndian(tableRead.AsSpan(12)));
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_ZeroLengthTable_ReturnsNoRegionsWithoutReading()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloMemoryDebug());
        transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, 0));
        var sahara = new SaharaProtocol(transport);

        var regions = await sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(regions);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_ImageModeHello_ThrowsBeforeAnswering()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(mode: 0));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("memory-debug HELLO", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_FirehoseXmlFirstBytes_Throws()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(firehoseXml: true));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Firehose XML", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_TableLengthNotRecordAligned_Throws()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloMemoryDebug());
        transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, 64 + 4));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("whole number of 64-byte records", exception.Message, StringComparison.Ordinal);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_TableBeyondBoundedBuffer_Throws()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloMemoryDebug());
        transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, 2L * 1024 * 1024));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("1048576 byte", exception.Message, StringComparison.Ordinal);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_ReadRequestCommandInsteadOfTable_Throws()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloMemoryDebug());
        transport.Enqueue(BuildSimplePacket(0x03, 20));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Expected Sahara MEMORY_DEBUG", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_TwoMegabyteRegion_RequestsOneMegabyteChunks()
    {
        var first = BuildPattern(ChunkBytes, seed: 7);
        var second = BuildPattern(ChunkBytes, seed: 131);
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(first);
        transport.Enqueue(second);
        var sahara = new SaharaProtocol(transport);
        using var destination = new MemoryStream();

        var written = await sahara.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x8000_0000, Length = 2L * ChunkBytes, Description = "ddr", FileName = "ddr.bin" },
            destination,
            TestContext.Current.CancellationToken);

        Assert.Equal(2L * ChunkBytes, (long)written);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(0x0Au, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0]));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0].AsSpan(4)));
        Assert.Equal(0x8000_0000u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0].AsSpan(8)));
        Assert.Equal((uint)ChunkBytes, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0].AsSpan(12)));
        Assert.Equal(0x8000_0000u + (uint)ChunkBytes, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(8)));
        Assert.Equal(first.Concat(second).ToArray(), destination.ToArray());
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_AddressBeyondThirtyTwoBits_UsesMemoryRead64()
    {
        var payload = BuildPattern(4096, seed: 3);
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(payload);
        var sahara = new SaharaProtocol(transport);
        using var destination = new MemoryStream();

        await sahara.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x1_0000_0000, Length = 4096, Description = "high", FileName = "high.bin" },
            destination,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(transport.Writes);
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(request));
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4)));
        Assert.Equal(0x1_0000_0000UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(8)));
        Assert.Equal(4096UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(16)));
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_ZeroLengthRegion_WritesNothing()
    {
        using var transport = new ScriptedCommTransport();
        var sahara = new SaharaProtocol(transport);
        using var destination = new MemoryStream();

        var written = await sahara.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x1000, Length = 0, Description = "empty", FileName = "empty.bin" },
            destination,
            TestContext.Current.CancellationToken);

        Assert.Equal(0UL, written);
        Assert.Empty(destination.ToArray());
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_RegionAboveConfiguredLimit_ThrowsWithoutSending()
    {
        using var transport = new ScriptedCommTransport();
        var sahara = new SaharaProtocol(transport) { MaxMemoryDumpRegionBytes = 1024 };
        using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<SaharaException>(() => sahara.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x1000, Length = 4096, Description = "big", FileName = "big.bin" },
            destination,
            TestContext.Current.CancellationToken));

        Assert.Contains("1024 byte dump limit", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_TargetReturnsTruncatedData_ThrowsAfterPartialWrite()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildPattern(1024, seed: 5));
        var sahara = new SaharaProtocol(transport);
        using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<SaharaException>(() => sahara.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x1000, Length = 4096, Description = "short", FileName = "short.bin" },
            destination,
            TestContext.Current.CancellationToken));

        Assert.Contains("returned only 1024 of 4096", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1024, destination.ToArray().Length);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_CancellationRequestedBeforeRead_SendsNoRequest()
    {
        using var transport = new ScriptedCommTransport();
        var sahara = new SaharaProtocol(transport);
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sahara.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x1000, Length = 4096, Description = "cancelled", FileName = "cancel.bin" },
            destination,
            cancellation.Token));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_FilePath_PublishesCompleteFileAndRemovesPartial()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var payload = BuildPattern(4096, seed: 11);
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(payload);
            var sahara = new SaharaProtocol(transport);
            var target = Path.Combine(directory, "tz.bin");

            await sahara.DumpMemoryRegionAsync(
                new SaharaMemoryRegion { Address = 0x1000, Length = 4096, Description = "tz", FileName = "tz.bin" },
                target,
                TestContext.Current.CancellationToken);

            Assert.Equal(payload, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(target + ".partial"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_FailedDump_KeepsExistingFileUnchanged()
    {
        var directory = CreateOutputDirectory();
        var target = Path.Combine(directory, "sbl.bin");
        try
        {
            await File.WriteAllBytesAsync(target, [1, 2, 3], TestContext.Current.CancellationToken);
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildPattern(512, seed: 17));
            var sahara = new SaharaProtocol(transport);

            await Assert.ThrowsAsync<SaharaException>(() => sahara.DumpMemoryRegionAsync(
                new SaharaMemoryRegion { Address = 0x1000, Length = 4096, Description = "sbl", FileName = "sbl.bin" },
                target,
                TestContext.Current.CancellationToken));

            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(target + ".partial"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CollectMemoryDumpAsync_TwoRegions_WritesBothAndResetsTarget()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var first = BuildPattern(256, seed: 21);
            var second = BuildPattern(128, seed: 22);
            var table = Concat(
                BuildRecord64(1, 0x1000, 256, "Secure Boot Loader", "sbl.bin"),
                BuildRecord64(2, 0x2000, 128, "Trust Zone", "tz.bin"),
                BuildRecord64(3, 0x3000, 0, "Empty segment", "empty.bin"));
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHelloMemoryDebug());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            transport.Enqueue(first);
            transport.Enqueue(second);
            transport.Enqueue(BuildSimplePacket(0x08, 8));
            var sahara = new SaharaProtocol(transport);

            var written = await sahara.CollectMemoryDumpAsync(directory, TestContext.Current.CancellationToken);

            Assert.Equal(2, written.Count);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(directory, "sbl.bin"), TestContext.Current.CancellationToken));
            Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(directory, "tz.bin"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));

            var reset = transport.Writes[^1];
            Assert.Equal(0x07u, BinaryPrimitives.ReadUInt32LittleEndian(reset));
            Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(reset.AsSpan(4)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CollectMemoryDumpAsync_UnsafeRegionFileName_ThrowsAndWritesNothing()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var table = Concat(
                BuildRecord64(1, 0x1000, 256, "Escape", "../evil.bin"),
                BuildRecord64(2, 0x2000, 128, "Console", "CON.bin"));
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHelloMemoryDebug());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            var sahara = new SaharaProtocol(transport);

            var exception = await Assert.ThrowsAsync<SaharaException>(
                () => sahara.CollectMemoryDumpAsync(directory, TestContext.Current.CancellationToken));

            Assert.Contains("unsafe region file name", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("sbl.bin", true)]
    [InlineData("mdlog_0.bin", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../evil.bin", false)]
    [InlineData(@"sub\dir.bin", false)]
    [InlineData("/absolute.bin", false)]
    [InlineData("trailing.", false)]
    [InlineData("CON.bin", false)]
    [InlineData("nul", false)]
    [InlineData("COM3.bin", false)]
    [InlineData("tab\tname.bin", false)]
    public void IsSafeMemoryDumpFileName_EvaluatesDeviceProposedNames(string fileName, bool expected)
        => Assert.Equal(expected, SaharaProtocol.IsSafeMemoryDumpFileName(fileName));

    [Fact]
    public void MemoryDebugPacket_Layouts_MatchReferenceWireSizes()
    {
        Assert.Equal(16, Marshal.SizeOf<SaharaMemoryDebugPacket>());
        Assert.Equal(24, Marshal.SizeOf<SaharaMemoryDebug64Packet>());
        Assert.Equal(16, Marshal.SizeOf<SaharaMemoryReadPacket>());
        Assert.Equal(24, Marshal.SizeOf<SaharaMemoryRead64Packet>());
        Assert.Equal(8, Marshal.SizeOf<SaharaResetPacket>());
    }

    private static byte[] BuildHello(uint mode = 2, bool firehoseXml = false)
    {
        var hello = new byte[48];
        if (firehoseXml)
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>"u8.CopyTo(hello);
            return hello;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(hello, 0x01);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(20), mode);
        return hello;
    }

    private static byte[] BuildHelloMemoryDebug() => BuildHello();

    private static byte[] BuildMemoryDebugPacket(uint address, uint length)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0x09);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), address);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), length);
        return packet;
    }

    private static byte[] BuildMemoryDebug64Packet(ulong address, ulong length)
    {
        var packet = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), address);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), length);
        return packet;
    }

    private static byte[] BuildSimplePacket(uint command, uint length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), length);
        return packet;
    }

    private static byte[] BuildRecord64(ulong savePreference, ulong address, ulong length, string description, string fileName)
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(record, savePreference);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), address);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), length);
        WriteFixedField(record, 24, description);
        WriteFixedField(record, 44, fileName);
        return record;
    }

    private static byte[] BuildRecord32(uint savePreference, uint address, uint length, string description, string fileName)
    {
        var record = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(record, savePreference);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), address);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), length);
        WriteFixedField(record, 12, description);
        WriteFixedField(record, 32, fileName);
        return record;
    }

    private static void WriteFixedField(byte[] record, int offset, string value)
        => Encoding.ASCII.GetBytes(value).CopyTo(record.AsSpan(offset, 20));

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(part => part.Length);
        var result = new byte[total];
        var position = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result.AsSpan(position));
            position += part.Length;
        }

        return result;
    }

    private static byte[] BuildPattern(int length, byte seed)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index * 31 + seed) % 251);
        }

        return data;
    }

    private static string CreateOutputDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-sahara-dump-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}