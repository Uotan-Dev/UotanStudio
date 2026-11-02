using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Replays scripted Sahara byte streams and records every write the protocol issues.
/// <para>回放脚本化的 Sahara 字节流，并记录协议发出的每次写入。</para>
/// </summary>
public sealed class ScriptedSaharaTransport : ICommTransport
{
    private readonly Queue<byte[]> _reads = new();
    private byte[]? _carry;
    public List<byte[]> Writes { get; } = [];

    public bool IsConnected => true;

    public void Connect()
    {
    }

    public void Disconnect()
    {
    }

    public void Reset()
    {
    }

    public void EnqueueRead(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            _reads.Enqueue(chunk);
        }
    }

    public int Read(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (_carry is { Length: > 0 } carry)
        {
            var take = Math.Min(length, carry.Length);
            carry.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
            _carry = take < carry.Length ? carry[take..] : null;
            return Task.FromResult(take);
        }

        if (_reads.Count == 0)
        {
            throw new TimeoutException();
        }

        var chunk = _reads.Dequeue();
        var copy = Math.Min(length, chunk.Length);
        chunk.AsSpan(0, copy).CopyTo(buffer.AsSpan(offset));
        _carry = copy < chunk.Length ? chunk[copy..] : null;
        return Task.FromResult(copy);
    }

    public long Write(byte[] data, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        Writes.Add(data.AsSpan(offset, length).ToArray());
        return Task.FromResult((long)length);
    }

    public Stream GetStream() => throw new NotSupportedException();

    public void Dispose()
    {
    }
}

public class SaharaRamDumpTests
{
    private const uint Hello = 0x01;
    private const uint HelloResponse = 0x02;
    private const uint Reset = 0x07;
    private const uint ResetResponse = 0x08;
    private const uint MemoryDebug64 = 0x10;
    private const uint MemoryRead64 = 0x11;

    private static byte[] HelloPacket(uint mode)
    {
        var packet = new byte[0x30];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Hello);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x14), mode);
        return packet;
    }

    private static byte[] MemoryDebug64Packet(ulong address, ulong length)
    {
        var packet = new byte[0x18];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, MemoryDebug64);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), address);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(0x10), length);
        return packet;
    }

    private static byte[] ResetResponsePacket()
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, ResetResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 8);
        return packet;
    }

    private static byte[] DebugTable(ulong address, ulong length, string typeName, string fileName)
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(record, 0x1);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), address);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), length);
        Encoding.ASCII.GetBytes(typeName).CopyTo(record.AsSpan(24));
        Encoding.ASCII.GetBytes(fileName).CopyTo(record.AsSpan(44));
        return record;
    }

    [Fact]
    public async Task CollectRamDumpAsync_MemoryDebugHelloAndOneRegion_WritesRegionFileAndResets()
    {
        var transport = new ScriptedSaharaTransport();
        var regionData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var table = DebugTable(0x1000, (ulong)regionData.Length, "DDR", "DDR.BIN");
        transport.EnqueueRead(
            HelloPacket(2),
            MemoryDebug64Packet(0x2000, (ulong)table.Length),
            table,
            regionData,
            ResetResponsePacket());

        var sahara = new SaharaProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("uotan-ramdump-");
        try
        {
            var regions = await sahara.CollectRamDumpAsync(directory.FullName,
                cancellationToken: TestContext.Current.CancellationToken);

            var region = Assert.Single(regions);
            Assert.Equal("DDR.BIN", region.FileName);
            Assert.Equal(0x1000UL, region.Address);
            Assert.Equal((ulong)regionData.Length, region.Length);
            Assert.Equal(regionData, await File.ReadAllBytesAsync(
                Path.Combine(directory.FullName, "DDR.BIN"), TestContext.Current.CancellationToken));

            var firstWrite = transport.Writes[0];
            Assert.Equal(HelloResponse, BinaryPrimitives.ReadUInt32LittleEndian(firstWrite));
            Assert.Equal(0x30u, BinaryPrimitives.ReadUInt32LittleEndian(firstWrite.AsSpan(4)));

            var reads = transport.Writes.Where(w => BinaryPrimitives.ReadUInt32LittleEndian(w) == MemoryRead64).ToList();
            Assert.Equal(2, reads.Count);
            Assert.Equal(0x2000UL, BinaryPrimitives.ReadUInt64LittleEndian(reads[0].AsSpan(8)));
            Assert.Equal(0x1000UL, BinaryPrimitives.ReadUInt64LittleEndian(reads[1].AsSpan(8)));

            Assert.Equal(Reset, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[^1]));
        }
        finally
        {
            sahara.Dispose();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CollectRamDumpAsync_SegmentFilter_KeepsOnlyMatchingRegion()
    {
        var transport = new ScriptedSaharaTransport();
        var table = new byte[128];
        DebugTable(0x1000, 4, "DDR", "DDR.BIN").CopyTo(table, 0);
        DebugTable(0x2000, 4, "OCIMEM", "OCIMEM.BIN").CopyTo(table, 64);
        transport.EnqueueRead(
            HelloPacket(2),
            MemoryDebug64Packet(0x3000, 128),
            table,
            new byte[] { 9, 9, 9, 9 },
            ResetResponsePacket());

        var sahara = new SaharaProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("uotan-ramdump-filter-");
        try
        {
            var regions = await sahara.CollectRamDumpAsync(directory.FullName, "ocimem",
                cancellationToken: TestContext.Current.CancellationToken);

            var region = Assert.Single(regions);
            Assert.Equal("OCIMEM.BIN", region.FileName);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "DDR.BIN")));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "OCIMEM.BIN")));
        }
        finally
        {
            sahara.Dispose();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CollectRamDumpAsync_ThirtyTwoBitMemoryDebug_ThrowsWithoutWritingFiles()
    {
        var transport = new ScriptedSaharaTransport();
        var packet = new byte[0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0x09);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x10);
        transport.EnqueueRead(HelloPacket(2), packet);

        var sahara = new SaharaProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("uotan-ramdump-32-");
        try
        {
            await Assert.ThrowsAsync<SaharaException>(() =>
                sahara.CollectRamDumpAsync(directory.FullName,
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory.FullName));
        }
        finally
        {
            sahara.Dispose();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CollectRamDumpAsync_ImageTransferHello_Throws()
    {
        var transport = new ScriptedSaharaTransport();
        transport.EnqueueRead(HelloPacket(0));

        var sahara = new SaharaProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("uotan-ramdump-mode-");
        try
        {
            await Assert.ThrowsAsync<SaharaException>(() =>
                sahara.CollectRamDumpAsync(directory.FullName,
                    cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            sahara.Dispose();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CollectRamDumpAsync_FileNameEscapesDirectory_ThrowsBeforeWriting()
    {
        var transport = new ScriptedSaharaTransport();
        var table = DebugTable(0x1000, 4, "DDR", "..\\escape.bin");
        transport.EnqueueRead(
            HelloPacket(2),
            MemoryDebug64Packet(0x2000, (ulong)table.Length),
            table);

        var sahara = new SaharaProtocol(transport);
        var directory = Directory.CreateTempSubdirectory("uotan-ramdump-escape-");
        try
        {
            await Assert.ThrowsAsync<SaharaException>(() =>
                sahara.CollectRamDumpAsync(directory.FullName,
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory.FullName));
        }
        finally
        {
            sahara.Dispose();
            directory.Delete(true);
        }
    }
}