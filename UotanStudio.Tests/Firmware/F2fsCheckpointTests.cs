using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks current checkpoint and NAT state from independent disk patches. <para>通过独立磁盘补丁检查当前 checkpoint 和 NAT 状态。</para></summary>
public class F2fsCheckpointTests
{
    [Fact]
    public void ExtractionFailurePreservesTheExistingFile()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        File.WriteAllBytes(Path.Combine(source.Path, "file.bin"), new byte[8192]);
        using var image = new MemoryStream(F2fsBuilder.Build(source.Path));
        using var parser = new F2fsParser(image);
        parser.ListFiles();
        image.SetLength(4096);
        var existing = Path.Combine(target.Path, "file.bin");
        File.WriteAllText(existing, "existing");
        Assert.ThrowsAny<Exception>(() => parser.ExtractToDirectory(target.Path));
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Single(Directory.EnumerateFiles(target.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerSecondCheckpointReadsNewNatVersionOrJournal(bool journal)
    {
        using var source = new TempDirectory();
        File.WriteAllText(Path.Combine(source.Path, "test.txt"), "old-data", new UTF8Encoding(false));
        var image = F2fsBuilder.Build(source.Path);
        var sb = F2fsSuperblock.FromBytes(image.AsSpan(1024));
        var oldNode = Read32(image, (int)sb.NatBlkaddr * 4096 + 4 * 9 + 5);
        var newNode = (uint)(image.Length / 4096 - 1);
        image.AsSpan((int)oldNode * 4096, 4096).CopyTo(image.AsSpan((int)newNode * 4096));
        "new-data"u8.CopyTo(image.AsSpan((int)newNode * 4096 + 364));
        var cp = checked((int)(sb.CpBlkaddr + 512) * 4096);
        Write64(image, cp, 2);
        if (journal)
        {
            var summary = cp + 4096;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(summary), 1);
            Write32(image, summary + 2, 4);
            Write32(image, summary + 7, 4);
            Write32(image, summary + 11, newNode);
        }
        else
        {
            var secondNat = (int)(sb.NatBlkaddr + 512) * 4096;
            image.AsSpan((int)sb.NatBlkaddr * 4096, 4096).CopyTo(image.AsSpan(secondNat));
            Write32(image, secondNat + 4 * 9 + 5, newNode);
            var natBitmap = cp + 192 + (int)Read32(image, cp + 156);
            image[natBitmap] = 0x80;
        }
        UpdateCheckpoint(image, cp);
        using var parser = new F2fsParser(new MemoryStream(image));
        using var output = new MemoryStream();
        parser.ReadFile("test.txt", output);
        Assert.Equal("new-data", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void InvalidCheckpointFallsBackAndBothInvalidAreRejected()
    {
        using var source = new TempDirectory();
        File.WriteAllText(Path.Combine(source.Path, "test.txt"), "data");
        var image = F2fsBuilder.Build(source.Path);
        var sb = F2fsSuperblock.FromBytes(image.AsSpan(1024));
        image[(int)sb.CpBlkaddr * 4096] ^= 1;
        using (var parser = new F2fsParser(new MemoryStream(image))) Assert.Single(parser.ListFiles());
        image[(int)(sb.CpBlkaddr + 512) * 4096] ^= 1;
        Assert.Throws<InvalidDataException>(() => new F2fsParser(new MemoryStream(image)));
    }

    [Fact]
    public void NodeSlotsUseTwoDirectTwoIndirectOneDoubleIndirect()
    {
        using var source = new TempDirectory();
        File.WriteAllText(Path.Combine(source.Path, "test.txt"), "data");
        var image = F2fsBuilder.Build(source.Path);
        var sb = F2fsSuperblock.FromBytes(image.AsSpan(1024));
        var firstBlock = (uint)(image.Length / 4096 - 12);
        // Inode slot 3: indirect -> direct -> data. Slot 4 adds one indirection.
        for (uint nid = 10; nid <= 14; nid++) Write32(image, (int)sb.NatBlkaddr * 4096 + (int)nid * 9 + 5, firstBlock + nid - 10);
        Write32(image, (int)firstBlock * 4096, 11);
        Write32(image, (int)(firstBlock + 1) * 4096, firstBlock + 10);
        Write32(image, (int)(firstBlock + 2) * 4096, 13);
        Write32(image, (int)(firstBlock + 3) * 4096, 14);
        Write32(image, (int)(firstBlock + 4) * 4096, firstBlock + 11);
        var inode = new F2fsInode { Addr = new uint[923], Nid = [0, 0, 0, 10, 12] };
        using var parser = new F2fsParser(new MemoryStream(image));
        const uint indirectCapacity = 1018 * 1018;
        Assert.Equal(firstBlock + 10, parser.MapBlock(inode, 923 + 2 * 1018 + indirectCapacity));
        Assert.Equal(firstBlock + 11, parser.MapBlock(inode, 923 + 2 * 1018 + 2 * indirectCapacity));
    }

    private static uint Read32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static void Write32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Write64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);

    private static void UpdateCheckpoint(byte[] image, int offset)
    {
        var checksumOffset = (int)Read32(image, offset + 164);
        var crc = 0xF2F52010u;
        foreach (var value in image.AsSpan(offset, checksumOffset))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        Write32(image, offset + checksumOffset, crc);
        var blocks = (int)Read32(image, offset + 136);
        image.AsSpan(offset, 4096).CopyTo(image.AsSpan(offset + (blocks - 1) * 4096));
    }
}