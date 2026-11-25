using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class Ext4MultiGroupInodeTests
{
    [Fact]
    public void Parser_ReadsFileWhoseInodeLivesInSecondBlockGroup()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-ext4-mg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "image.img");
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(src, $"f{i}.txt"), $"data{i}");
            Ext4Builder.Build(src, imagePath, new Ext4Options
            {
                IncludeJournal = false,
                EnableMetadataCsum = false,
                TargetSizeBytes = 132 * 1024 * 1024,
            });
            var image = File.ReadAllBytes(imagePath);

            var blockSize = 4096;
            var inodeSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 88, 2));
            if (inodeSize == 0) inodeSize = 256;
            var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 40, 4));
            var gdtOffset = blockSize;
            var descSize = (int)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 254, 2));
            if (descSize == 0) descSize = 32;
            var group0Table = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(gdtOffset + 8, 4)) * blockSize;
            var group1Table = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(gdtOffset + descSize + 8, 4)) * blockSize;
            Assert.True(group1Table > group0Table);

            var originalInode = 5u;
            var movedInode = inodesPerGroup + 1;
            image.AsSpan(group0Table + (int)(originalInode - 1) * inodeSize, inodeSize)
                .CopyTo(image.AsSpan(group1Table, inodeSize));

            var rootDataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(group0Table + inodeSize + 40 + 20, 4));
            var dirOffset = rootDataBlock * blockSize;
            var patched = false;
            for (var pos = 0; pos < blockSize;)
            {
                var recLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dirOffset + pos + 4, 2));
                if (recLen < 8) break;
                var nameLen = image[dirOffset + pos + 6];
                var name = Encoding.UTF8.GetString(image, dirOffset + pos + 8, nameLen);
                if (name == "f4.txt")
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dirOffset + pos, 4), movedInode);
                    patched = true;
                    break;
                }
                pos += recLen;
            }
            Assert.True(patched, "test setup: directory entry not found");

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var entries = parser.ListFiles();
            Assert.Contains(entries, e => e.Path == "f4.txt");
        }
        finally { Directory.Delete(root, true); }
    }
}