using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Ext4.Models;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FileSystemLayoutRegressionTests
{
    [Fact]
    public void ExtSuperblock_ReadsUpstreamHashAndChecksumOffsets()
    {
        var image = new byte[2048];
        var sb = image.AsSpan(1024);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[56..], 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], 32768);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 8192);
        sb[234] = 0xAA;
        sb[252] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(sb[1020..], 0x12345678);
        var parsed = Ext4Superblock.FromBytes(image);
        Assert.Equal(2, parsed.DefHashVersion);
        Assert.Equal(0x12345678u, parsed.Checksum);
    }

    [Fact]
    public void F2fsInode_PreservesUpstream64BitTimestamps()
    {
        var page = new byte[4096];
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(40), 0x100000001UL);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(48), 0x100000002UL);
        var inode = F2fsInode.FromBytes(page);
        Assert.Equal(0x100000001UL, inode.Ctime);
        Assert.Equal(0x100000002UL, inode.Mtime);
        var written = new byte[4096];
        inode.WriteTo(written);
        Assert.Equal(page.AsSpan(40, 16).ToArray(), written.AsSpan(40, 16).ToArray());
    }

    [Fact]
    public void F2fsBuilder_PreservesModificationTimeBeyond32Bits()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "future.txt");
        File.WriteAllText(file, "future timestamp");
        var timestamp = new DateTimeOffset(2107, 1, 2, 3, 4, 5, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(file, timestamp.UtcDateTime);
        var image = Path.Combine(temp.Path, "image.f2fs");
        F2fsBuilder.Build(source, image);
        uint nid;
        using (var parser = new F2fsParser(image))
            nid = Assert.Single(parser.ListFiles()).Inode;
        using var stream = File.OpenRead(image);
        var header = new byte[4096];
        stream.ReadExactly(header);
        var sb = F2fsSuperblock.FromImage(header);
        Assert.True(nid < F2fsNode.NatEntryPerBlock);
        stream.Position = (long)sb.NatBlkaddr * 4096 + nid * F2fsNode.NatEntrySize + F2fsNode.NatEntryBlockOffset;
        var address = new byte[4];
        stream.ReadExactly(address);
        stream.Position = (long)BinaryPrimitives.ReadUInt32LittleEndian(address) * 4096 + 48;
        var seconds = new byte[8];
        stream.ReadExactly(seconds);
        Assert.Equal((ulong)timestamp.ToUnixTimeSeconds(), BinaryPrimitives.ReadUInt64LittleEndian(seconds));
    }
}