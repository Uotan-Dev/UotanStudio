using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlMemoryDumpServiceTests
{
    private const ulong TableAddress = 0x8F00_0000UL;

    [Fact]
    public async Task DumpCoreAsync_UnconfirmedRequest_ThrowsBeforeDeviceInteraction()
    {
        var directory = CreateOutputDirectory();
        try
        {
            using var transport = new ScriptedCommTransport();
            using var session = new EdlSession(transport);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EdlMemoryDumpService.DumpCoreAsync(session, directory, confirmed: false, null,
                    TestContext.Current.CancellationToken));

            Assert.Contains("explicit confirmation", exception.Message, StringComparison.Ordinal);
            Assert.Empty(transport.Writes);
            Assert.Equal(0, transport.ReadCount);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpCoreAsync_CancelledToken_ThrowsBeforeAnyRegionIsListed()
    {
        var directory = CreateOutputDirectory();
        try
        {
            using var transport = new ScriptedCommTransport();
            using var session = new EdlSession(transport);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EdlMemoryDumpService.DumpCoreAsync(
                session, directory, confirmed: true, null, cancellation.Token));

            Assert.Empty(transport.Writes);
            Assert.Equal(0, transport.ReadCount);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpCoreAsync_UnsafeRegionFileName_RefusesAndWritesNothing()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var table = Concat(
                BuildRecord64(1, 0x1000, 256, "Escape", "../evil.bin"),
                BuildRecord64(2, 0x2000, 128, "Console", "CON.bin"));
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHello());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            using var session = new EdlSession(transport);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                EdlMemoryDumpService.DumpCoreAsync(session, directory, confirmed: true, null,
                    TestContext.Current.CancellationToken));

            Assert.Contains("unsafe region file name", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Equal(3, transport.Writes.Count);
            AssertResetPacket(transport.Writes[^1]);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpCoreAsync_TwoRegionsWithTheSameFileName_RefusesTheSecondBeforeReadingIt()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var table = Concat(
                BuildRecord64(1, 0x1000, 256, "Secure Boot Loader", "sbl.bin"),
                BuildRecord64(2, 0x2000, 128, "Trust Zone", "sbl.bin"));
            var first = BuildPattern(256, seed: 21);
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHello());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            transport.Enqueue(first);
            using var session = new EdlSession(transport);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => EdlMemoryDumpService.DumpCoreAsync(
                session, directory, confirmed: true, null, TestContext.Current.CancellationToken));

            Assert.Contains("resolve to the file name 'sbl.bin'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(4, transport.Writes.Count);
            AssertResetPacket(transport.Writes[^1]);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(directory, "sbl.bin"),
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.uotan-*"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpCoreAsync_BlankOutputDirectory_ThrowsBeforeDeviceInteraction()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => EdlMemoryDumpService.DumpCoreAsync(
            session, "  ", confirmed: true, null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task DumpCoreAsync_TwoNonEmptyRegions_PublishesVerifiedBytesAndProgress()
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
            var frames = new List<FlashProgress>();
            var progress = new RecordingProgress(frames);

            var result = await EdlMemoryDumpService.DumpCoreAsync(session, directory, confirmed: true, progress,
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Contains("2 Sahara memory-debug region(s)", result.Summary, StringComparison.Ordinal);
            Assert.Contains("384", result.Summary, StringComparison.Ordinal);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(directory, "sbl.bin"),
                TestContext.Current.CancellationToken));
            Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(directory, "tz.bin"),
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.uotan-*"));
            Assert.Equal(5, transport.Writes.Count);
            AssertResetPacket(transport.Writes[^1]);
            Assert.Equal(6, frames.Count);
            Assert.Equal(100, frames[^1].Percent);
            Assert.Equal("sbl.bin", frames[1].Partition);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpCoreAsync_TargetReturnsFewerBytesThanDeclared_KeepsExistingFileAndStaging()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var destination = Path.Combine(directory, "sbl.bin");
            await File.WriteAllBytesAsync(destination, [1, 2, 3], TestContext.Current.CancellationToken);
            var table = BuildRecord64(1, 0x1000, 4096, "Secure Boot Loader", "sbl.bin");
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHello());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            transport.Enqueue(BuildPattern(1024, seed: 5));
            using var session = new EdlSession(transport);

            await Assert.ThrowsAsync<SaharaException>(() => EdlMemoryDumpService.DumpCoreAsync(
                session, directory, confirmed: true, null, TestContext.Current.CancellationToken));

            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.uotan-*"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DumpCoreAsync_OnlyEmptyRegions_ReportsFailureWithoutWriting()
    {
        var directory = CreateOutputDirectory();
        try
        {
            var table = BuildRecord64(1, 0x1000, 0, "Empty segment", "empty.bin");
            using var transport = new ScriptedCommTransport();
            transport.Enqueue(BuildHello());
            transport.Enqueue(BuildMemoryDebug64Packet(TableAddress, (ulong)table.Length));
            transport.Enqueue(table);
            transport.Enqueue(BuildSimplePacket(0x08, 8));
            using var session = new EdlSession(transport);

            var result = await EdlMemoryDumpService.DumpCoreAsync(session, directory, confirmed: true, null,
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("no non-empty memory-debug region", result.Summary, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Equal(3, transport.Writes.Count);
            AssertResetPacket(transport.Writes[^1]);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("sbl.bin", true)]
    [InlineData("mdlog_0.bin", true)]
    [InlineData("../evil.bin", false)]
    [InlineData(@"sub\dir.bin", false)]
    [InlineData("CON.bin", false)]
    [InlineData("", false)]
    public void ResolveRegionPath_EvaluatesTargetProposedNames(string fileName, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-edl-dump-root");
        var region = new SaharaMemoryRegion
        {
            Address = 0x1000,
            Length = 256,
            Description = "region",
            FileName = fileName
        };

        if (!accepted)
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => EdlMemoryDumpService.ResolveRegionPath(root, region));

            Assert.Contains("unsafe region file name", exception.Message, StringComparison.Ordinal);
            return;
        }

        Assert.Equal(Path.Combine(root, fileName), EdlMemoryDumpService.ResolveRegionPath(root, region));
    }

    [Fact]
    public void ResolveRegionPath_BlankOutputRoot_IsRejected()
    {
        var region = new SaharaMemoryRegion
        {
            Address = 0x1000,
            Length = 256,
            Description = "region",
            FileName = "sbl.bin"
        };

        Assert.Throws<ArgumentException>(() => EdlMemoryDumpService.ResolveRegionPath("  ", region));
    }

    private static void AssertResetPacket(byte[] packet)
    {
        Assert.Equal(8, packet.Length);
        Assert.Equal(0x07u, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
    }

    private static byte[] BuildSimplePacket(uint command, uint length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), length);
        return packet;
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
        var directory = Path.Combine(Path.GetTempPath(), "uotan-edl-memory-dump-" + Guid.NewGuid().ToString("N"));
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

    private sealed class RecordingProgress(List<FlashProgress> sink) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => sink.Add(value);
    }
}