using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsSharedXattrReadTests
{
    [Fact]
    public void SharedAttribute_CrossingBlockBoundary_IsReadWithoutWholeAreaAllocation()
    {
        byte[] image = BuildImage(false);
        using var source = new CountingSource(image);
        using var parser = new ErofsParser(source);
        var files = parser.EnumerateFiles();
        Assert.Equal(2, files.Count);
        foreach (var file in files)
        {
            var attr = Assert.Single(file.Xattrs!);
            Assert.Equal("security.selinux", attr.Name);
            Assert.Equal("u:object_r:system_file:s0", Encoding.UTF8.GetString(attr.Value));
        }
        Assert.InRange(source.LargestRead, 1, 4096);
        Assert.InRange(source.TotalRead, 1, 16384);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(2093055u)]
    public void InvalidSharedId_LeavesFileDataReadable(uint id)
    {
        byte[] image = BuildImage(false);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4096 + 32 + 32 + 12), id);
        image[^4] = 255;
        image[^3] = 6;
        image[^2] = image[^1] = 255;
        using var source = new CountingSource(image);
        using var parser = new ErofsParser(source);
        var file = Assert.Single(parser.EnumerateFiles(), entry => entry.Path == "x");
        Assert.Null(file.Xattrs);
        Assert.Equal("first"u8.ToArray(), parser.ReadFileBytes("x"));
        Assert.InRange(source.LargestRead, 1, 4096);
    }

    [Fact]
    public void Extraction_DuplicatePaths_PreservesFirstMatchAndProgress()
    {
        using var temp = new TempDirectory();
        using var parser = new ErofsParser(BuildImage(true));
        var progress = new RecordingProgress();
        parser.ExtractToDirectory(temp.Path, progress: progress);
        Assert.Equal("first", File.ReadAllText(Path.Combine(temp.Path, "x")));
        Assert.Equal([(1, 2), (2, 2)], progress.Values);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    private static byte[] BuildImage(bool duplicatePath)
    {
        const int block = 4096;
        byte[] image = new byte[8 * 1024 * 1024];
        new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = 12,
            RootNid = 0,
            Inos = 3,
            Blocks = (uint)(image.Length / block),
            MetaBlkAddr = 1,
            XattrBlkAddr = 4
        }.WriteTo(image.AsSpan(1024, 128));
        new ErofsInode { Mode = ErofsInode.ModeDirectory, Size = 26, U = 6, Nlink = 2 }.WriteTo(image.AsSpan(block, 32));
        for (int i = 0; i < 2; i++)
        {
            int inodeOffset = block + 32 + i * 64;
            new ErofsInode { Mode = ErofsInode.ModeRegularFile, Size = 5, U = (uint)(2 + i), Nlink = 1, XattrICount = 2 }
                .WriteTo(image.AsSpan(inodeOffset, 32));
            image[inodeOffset + 32 + 4] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeOffset + 32 + 12), (block - 8) / 4);
            int dirent = 6 * block + i * 12;
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dirent), (ulong)(1 + i * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dirent + 8), (ushort)(24 + i));
            image[dirent + 10] = ErofsInode.FileTypeRegularFile;
            image[6 * block + 24 + i] = (byte)(duplicatePath || i == 0 ? 'x' : 'y');
        }
        "first"u8.CopyTo(image.AsSpan(2 * block));
        "other"u8.CopyTo(image.AsSpan(3 * block));
        int shared = 5 * block - 8;
        image[shared] = 7;
        image[shared + 1] = 6;
        byte[] value = "u:object_r:system_file:s0"u8.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(shared + 2), (ushort)value.Length);
        "selinux"u8.CopyTo(image.AsSpan(shared + 4));
        value.CopyTo(image, shared + 11);
        return image;
    }

    private sealed class CountingSource(byte[] image) : MemoryStream(image, writable: false)
    {
        internal int LargestRead { get; private set; }
        internal long TotalRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRead = Math.Max(LargestRead, count);
            int read = base.Read(buffer, offset, count);
            TotalRead += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            int read = base.Read(buffer);
            TotalRead += read;
            return read;
        }
    }

    private sealed class RecordingProgress : IProgress<(int processed, int total)>
    {
        internal List<(int, int)> Values { get; } = [];
        public void Report((int processed, int total) value) => Values.Add(value);
    }
}