using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsDirentConformanceTests
{
    [Fact]
    public void ParseBlock_NormalSingleBlockDirectory_EnumeratesRunningNameOffsets()
    {
        var block = BuildBlock("a", "bc", "def");

        var entries = ErofsDirectoryEntry.ParseBlock(block);

        Assert.Equal(3, entries.Count);
        Assert.Equal(["a", "bc", "def"], entries.Select(entry => entry.Name));
        Assert.Equal(36, (int)entries[0].NameOff);
        Assert.Equal(37, (int)entries[1].NameOff);
        Assert.Equal(39, (int)entries[2].NameOff);
        Assert.Equal(entries.Count * ErofsDirectoryEntry.DirentSize, (int)entries[0].NameOff);
        Assert.Equal([1ul, 2ul, 3ul], entries.Select(entry => entry.Nid));
        Assert.All(entries, entry => Assert.Equal(ErofsInode.FileTypeRegularFile, entry.FileType));
    }

    [Fact]
    public void ParseBlock_NameOffWithBitFifteenSet_ThrowsFileSystemCorruptedException()
    {
        var block = new byte[65536];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0xC000);

        Assert.Throws<FileSystemCorruptedException>(() => ErofsDirectoryEntry.ParseBlock(block));
    }

    [Fact]
    public void ParseBlock_NonContiguousNameOff_ThrowsFileSystemCorruptedException()
    {
        var block = BuildBlock("a", "bc", "def");
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(ErofsDirectoryEntry.DirentSize + 8), 24);

        Assert.Throws<FileSystemCorruptedException>(() => ErofsDirectoryEntry.ParseBlock(block));
    }

    [Fact]
    public void ParseBlock_NameLengthBeyondBlock_ThrowsFileSystemCorruptedException()
    {
        var block = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(ErofsDirectoryEntry.DirentSize + 8), 200);

        Assert.Throws<FileSystemCorruptedException>(() => ErofsDirectoryEntry.ParseBlock(block));
    }

    [Fact]
    public void ParseBlock_NameLengthAboveNameLimit_ThrowsFileSystemCorruptedException()
    {
        var block = new byte[4096];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(ErofsDirectoryEntry.DirentSize + 8), 400);

        Assert.Throws<FileSystemCorruptedException>(() => ErofsDirectoryEntry.ParseBlock(block));
    }

    private static byte[] BuildBlock(params string[] names)
    {
        var block = new byte[4096];
        var nameOff = names.Length * ErofsDirectoryEntry.DirentSize;
        for (var i = 0; i < names.Length; i++)
        {
            var slot = block.AsSpan(i * ErofsDirectoryEntry.DirentSize, ErofsDirectoryEntry.DirentSize);
            BinaryPrimitives.WriteUInt64LittleEndian(slot, (ulong)(i + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
            slot[10] = ErofsInode.FileTypeRegularFile;
            var bytes = Encoding.UTF8.GetBytes(names[i]);
            bytes.CopyTo(block.AsSpan(nameOff));
            nameOff += bytes.Length;
        }

        return block;
    }
}