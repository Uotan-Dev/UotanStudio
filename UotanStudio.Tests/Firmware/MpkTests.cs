using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Meizu.Mpk;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class MpkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-mpk-" + Guid.NewGuid().ToString("N"));

    public MpkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] BuildArchive(string identifier, string name, uint version, uint crc,
        params (string Path, byte[] Payload)[] files)
    {
        var archive = new byte[(int)(MpkFormat.HeaderSize + files.Length * MpkFormat.EntryRecordSize
            + files.Sum(f => (long)f.Payload.Length))];
        "!MPK"u8.CopyTo(archive);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(8), crc);
        Encoding.UTF8.GetBytes(identifier).CopyTo(archive, 12);
        Encoding.UTF8.GetBytes(name).CopyTo(archive, 172);
        "flyme"u8.CopyTo(archive.AsSpan(428));
        "17"u8.CopyTo(archive.AsSpan(436));
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(2044), (uint)files.Length);
        var dataOffset = MpkFormat.HeaderSize + files.Length * MpkFormat.EntryRecordSize;
        for (var index = 0; index < files.Length; index++)
        {
            var record = MpkFormat.HeaderSize + index * MpkFormat.EntryRecordSize;
            "!F"u8.CopyTo(archive.AsSpan(record));
            Encoding.UTF8.GetBytes(files[index].Path).CopyTo(archive, record + 2);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(record + 492), 0u);
            BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(record + 496), (ulong)dataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(record + 504), (ulong)files[index].Payload.Length);
            files[index].Payload.CopyTo(archive, dataOffset);
            dataOffset += files[index].Payload.Length;
        }

        return archive;
    }

    private async Task<string> WriteArchiveAsync(string fileName, byte[] archive)
    {
        var path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, archive, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public void Read_KnownArchive_ReturnsHeaderFieldsAndEntryRanges()
    {
        using var stream = new MemoryStream(BuildArchive("com.example.theme", "Demo", 3, 0xDEADBEEF,
            ("info", "data=1"u8.ToArray()), ("res/icon.png", [1, 2, 3])));

        var archive = MpkFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(3u, archive.Header.Version);
        Assert.Equal(0xDEADBEEFu, archive.Header.Crc32);
        Assert.Equal("com.example.theme", archive.Header.Identifier);
        Assert.Equal("Demo", archive.Header.Name);
        Assert.Equal("flyme", archive.Header.SupportSystem);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal("res/icon.png", archive.Entries[1].Path);
        Assert.Equal(2048 + 2 * 512L, archive.Entries[0].Offset);
        Assert.Equal(6, archive.Entries[0].Size);
        Assert.True(stream.Position == 0);
    }

    [Fact]
    public async Task CanHandle_ArchiveAndLookalikes_DecidesByMagicOnly()
    {
        var handler = new MpkFormatHandler();
        var archive = await WriteArchiveAsync("theme.mpk", BuildArchive("id", "n", 1, 0, ("a", [7])));
        var foreign = await WriteArchiveAsync("other.mpk", "PK\u0003\u0004garbage"u8.ToArray());

        Assert.True(handler.CanHandle(archive));
        Assert.False(handler.CanHandle(foreign));
        Assert.False(handler.CanHandle(Path.Combine(_root, "missing.mpk")));
    }

    [Fact]
    public async Task ReadMetadataAsync_ValidArchive_ReportsCrcAndIdentifier()
    {
        var path = await WriteArchiveAsync("meta.mpk", BuildArchive("com.acme", "Acme", 2, 0x12345678,
            ("info", "x"u8.ToArray()), ("assets/a.bin", [9, 9])));

        var result = await new MpkFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("0x12345678", result.Metadata["Crc32"]);
        Assert.Equal("com.acme", result.Metadata["Identifier"]);
        Assert.Equal("2", result.Metadata["FileCount"]);
        Assert.Contains("assets/a.bin", result.Entries);
    }

    [Fact]
    public async Task UnpackAsync_MultiEntryArchive_WritesByteIdenticalPayloads()
    {
        var payload = new byte[4096];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index * 31 & 0xFF);
        }

        var path = await WriteArchiveAsync("unpack.mpk", BuildArchive("id", "n", 1, 0,
            ("info", "hello"u8.ToArray()), ("assets/blob.dat", payload)));
        var output = Path.Combine(_root, "out");

        var result = await new MpkFormatHandler().UnpackAsync(path, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("hello"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(output, "info"), TestContext.Current.CancellationToken));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(output, "blob.dat"), TestContext.Current.CancellationToken));
        Assert.Equal(4096, Assert.Single(result.Components, c => c.Name == "blob.dat").SizeBytes);
    }

    [Fact]
    public async Task UnpackAsync_SelectedEntries_ExtractsOnlyRequestedPaths()
    {
        var path = await WriteArchiveAsync("select.mpk", BuildArchive("id", "n", 1, 0,
            ("a.txt", [1]), ("b.txt", [2])));
        var output = Path.Combine(_root, "select-out");

        var result = await new MpkFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { SelectedEntries = ["B.TXT"] }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal([2], await File.ReadAllBytesAsync(Path.Combine(output, "b.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(output, "a.txt")));
    }

    [Theory]
    [InlineData("truncated-table")]
    [InlineData("bad-entry-magic")]
    [InlineData("payload-past-end")]
    [InlineData("absurd-count")]
    [InlineData("empty-path")]
    public async Task Read_MalformedVariants_RejectsWithInvalidData(string failure)
    {
        var archive = BuildArchive("id", "n", 1, 0, ("info", new byte[64]), ("icon", new byte[32]));
        switch (failure)
        {
            case "truncated-table":
                archive = archive[..(MpkFormat.HeaderSize + 2 * MpkFormat.EntryRecordSize - 10)];
                break;
            case "bad-entry-magic":
                archive[MpkFormat.HeaderSize] = (byte)'X';
                break;
            case "payload-past-end":
                BinaryPrimitives.WriteUInt64LittleEndian(archive.AsSpan(MpkFormat.HeaderSize + 504), 100000);
                break;
            case "absurd-count":
                BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(2044), 4_000_000_000);
                break;
            case "empty-path":
                Array.Clear(archive, MpkFormat.HeaderSize + 2, 10);
                break;
        }

        using var stream = new MemoryStream(archive);

        await Task.CompletedTask;
        Assert.Throws<InvalidDataException>(() => MpkFormat.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_CorruptArchive_FailsInsteadOfThrowing()
    {
        var archive = BuildArchive("id", "n", 1, 0, ("info", [1]));
        archive[MpkFormat.HeaderSize] = (byte)'X';
        var path = await WriteArchiveAsync("corrupt.mpk", archive);

        var result = await new MpkFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task UnpackAsync_PreCanceledToken_ThrowsBeforePublishing()
    {
        var path = await WriteArchiveAsync("cancel.mpk", BuildArchive("id", "n", 1, 0, ("info", [1])));
        var output = Path.Combine(_root, "cancel-out");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MpkFormatHandler().UnpackAsync(path, output, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
    }

    [Fact]
    public void SafeEntryFileName_TraversalPaths_ReturnsBareName()
    {
        var name = MpkFormat.SafeEntryFileName(new MpkEntry("..\\..\\evil\\icon.png", 3072, 1, 0), 4);

        Assert.Equal("icon.png", name);
    }
}