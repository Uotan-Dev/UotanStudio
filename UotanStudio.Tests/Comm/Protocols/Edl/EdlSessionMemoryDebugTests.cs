using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlSessionMemoryDebugTests
{
    private const ulong TableAddress = 0x8F00_0000UL;

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_MemoryDebugHelloOnSession_ParsesTableWithoutFirehoseConfigure()
    {
        var table = Concat(
            BuildRecord64(1, 0x1000, 0x40, "Secure Boot Loader", "sbl.bin"),
            BuildRecord64(2, 0x2000, 0, "Empty segment", "empty.bin"));
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello());
        transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
        transport.Enqueue(table);
        using var session = new EdlSession(transport);

        var regions = await session.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, regions.Count);
        Assert.Equal(0x1000UL, regions[0].Address);
        Assert.Equal(0x40UL, regions[0].Length);
        Assert.Equal("sbl.bin", regions[0].FileName);
        Assert.False(session.IsConfigured);
        Assert.Equal(EdlLinkStage.Idle, session.LinkStage);
        Assert.NotNull(session.Sahara);

        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(0x02u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0]));
        var tableRead = transport.Writes[1];
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(tableRead));
        Assert.Equal(TableAddress, BinaryPrimitives.ReadUInt64LittleEndian(tableRead.AsSpan(8)));
        Assert.Equal((ulong)table.Length, BinaryPrimitives.ReadUInt64LittleEndian(tableRead.AsSpan(16)));
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_RegionFromSessionTable_StreamsRawBytesIntoCallerStream()
    {
        var table = BuildRecord64(1, 0x1000, 4096, "Trust Zone", "tz.bin");
        var payload = BuildPattern(4096, seed: 23);
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello());
        transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
        transport.Enqueue(table);
        using var session = new EdlSession(transport);
        var regions = await session.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(payload);
        using var destination = new MemoryStream();

        var written = await session.DumpMemoryRegionAsync(
            regions[0],
            destination,
            TestContext.Current.CancellationToken);

        Assert.Equal(4096UL, written);
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(3, transport.Writes.Count);
        var regionRead = transport.Writes[^1];
        Assert.Equal(0x11u, BinaryPrimitives.ReadUInt32LittleEndian(regionRead));
        Assert.Equal(0x1000UL, BinaryPrimitives.ReadUInt64LittleEndian(regionRead.AsSpan(8)));
        Assert.Equal(4096UL, BinaryPrimitives.ReadUInt64LittleEndian(regionRead.AsSpan(16)));
    }

    [Fact]
    public async Task CollectMemoryDumpAsync_TwoNonEmptyRegionsOnSession_WritesBothFilesAndResetsTarget()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var table = Concat(
                BuildRecord64(1, 0x1000, 256, "Secure Boot Loader", "sbl.bin"),
                BuildRecord64(2, 0x2000, 128, "Trust Zone", "tz.bin"),
                BuildRecord64(3, 0x3000, 0, "Empty segment", "empty.bin"));
            var first = BuildPattern(256, seed: 21);
            var second = BuildPattern(128, seed: 22);
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHello());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            transport.Enqueue(first);
            transport.Enqueue(second);
            transport.Enqueue(BuildSimplePacket(0x08, 8));
            using var session = new EdlSession(transport);

            var written = await session.CollectMemoryDumpAsync(directory, TestContext.Current.CancellationToken);

            Assert.Equal(2, written.Count);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(directory, "sbl.bin"),
                TestContext.Current.CancellationToken));
            Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(directory, "tz.bin"),
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
            var reset = transport.Writes[^1];
            Assert.Equal(0x07u, BinaryPrimitives.ReadUInt32LittleEndian(reset));
            Assert.Equal((uint)Marshal.SizeOf<SaharaResetPacket>(), BinaryPrimitives.ReadUInt32LittleEndian(reset.AsSpan(4)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_TargetNeverSendsAPacket_RefusesWithoutWriting()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);

        var exception = await Assert.ThrowsAsync<EdlException>(
            () => session.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ReadPortTimeout, exception.ErrorCode);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task CollectMemoryDumpAsync_MemoryDebugHelloWithoutTable_RefusesBeforeCollectingAnyRegion()
    {
        var directory = CreateOutputDirectory();
        try
        {
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHello());
            using var session = new EdlSession(transport);

            var exception = await Assert.ThrowsAsync<EdlException>(
                () => session.CollectMemoryDumpAsync(directory, TestContext.Current.CancellationToken));

            Assert.Equal(EdlErrorCode.ReadPortTimeout, exception.ErrorCode);
            Assert.Single(transport.Writes);
            Assert.Equal(0x02u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0]));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_ImageTransferHello_RefusesBeforeAnsweringTheTarget()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(mode: 0));
        using var session = new EdlSession(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => session.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("memory-debug HELLO", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ReadMemoryDebugRegionsAsync_LinkAlreadyProbedAsFirehose_RefusesBeforeTransportRead()
    {
        var transport = new ScriptedCommTransport
        {
            OnWrite = _ => [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" /></data>")]
        };
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.Firehose, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        var probeWrites = transport.Writes.Count;
        var readsBefore = transport.ReadCount;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ReadMemoryDebugRegionsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("crashdump link", exception.Message, StringComparison.Ordinal);
        Assert.Equal(probeWrites, transport.Writes.Count);
        Assert.Equal(readsBefore, transport.ReadCount);
    }

    [Fact]
    public async Task CollectMemoryDumpAsync_BlankOutputDirectory_ThrowsBeforeDeviceInteraction()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => session.CollectMemoryDumpAsync(
            "   ", TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_DisposedSession_ThrowsObjectDisposed()
    {
        using var transport = new ScriptedCommTransport();
        var session = new EdlSession(transport);
        session.Dispose();
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.DumpMemoryRegionAsync(
            new SaharaMemoryRegion { Address = 0x1000, Length = 4096, Description = "tz", FileName = "tz.bin" },
            destination,
            TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task DumpMemoryRegionAsync_NullRegion_ThrowsBeforeTransportRead()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() => session.DumpMemoryRegionAsync(
            null!, destination, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    private static byte[] BuildHello(uint mode = 2)
    {
        var hello = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(hello, 0x01);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(20), mode);
        return hello;
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

    private static byte[] BuildRecord64(ulong savePreference, ulong address, ulong length, string description,
        string fileName)
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(record, savePreference);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), address);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), length);
        WriteFixedField(record, 24, description);
        WriteFixedField(record, 44, fileName);
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
        var directory = Path.Combine(Path.GetTempPath(), "uotan-edl-session-dump-" + Guid.NewGuid().ToString("N"));
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