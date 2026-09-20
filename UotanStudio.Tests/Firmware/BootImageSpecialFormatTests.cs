using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.Archive;
using UotanStudio.Firmware.BoundedContexts.Archive.Editing;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BootImageSpecialFormatTests
{
    [Fact]
    public void TegraBlob_IsParsedWithCheckedComponentRanges()
    {
        var data = new byte[320];
        data[0] = 0x4E;
        data[1] = 0x56;
        data[2] = 0x44;
        data[3] = 0x41;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 260);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 320);
        data[256] = 1;
        data[257] = 2;
        data[258] = 3;
        data[259] = 4;
        data[260] = 5;

        var parsed = TegraBlobTool.Parse(data);

        Assert.True(parsed.IsValid);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed.Kernel);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, parsed.Ramdisk);
    }

    [Fact]
    public void TegraBlob_RejectsOverflowedComponentRange()
    {
        var data = new byte[256];
        data[0] = 0x4E;
        data[1] = 0x56;
        data[2] = 0x44;
        data[3] = 0x41;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 256);

        var parsed = TegraBlobTool.Parse(data);

        Assert.False(parsed.IsValid);
        Assert.Contains("range", parsed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZImage_UsesExistingCompressionDetection()
    {
        var data = new byte[80];
        data[0x24] = 0x18;
        data[0x25] = 0x28;
        data[0x26] = 0x6F;
        data[0x27] = 0x01;
        data[0x40] = 0x1F;
        data[0x41] = 0x8B;

        var parsed = ZImageTool.Parse(data);

        Assert.True(parsed.IsValid);
        Assert.Equal(CompressionType.Gzip, parsed.CompressionType);
        Assert.Equal(0x40, parsed.CompressedOffset);
        Assert.Equal(16, parsed.CompressedSize);
    }

    [Fact]
    public void CpioEditor_ReusesArchiveEntriesForRamdiskMutation()
    {
        var archive = new CpioArchive();
        using var source = new MemoryStream();
        archive.SaveAll(source,
        [new ArchiveEntry
        {
            Name = "./init.rc",
            Data = "old"u8.ToArray(),
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, UnixPermissions = 0x81A4, LinkCount = 1 }
        }], ArchiveFormat.NewAscii);
        source.Position = 0;

        var editor = new CpioArchiveEditor();
        editor.Load(source);
        Assert.True(editor.Rename("init.rc", "init.magisk.rc"));
        editor.AddOrReplace("overlay.rc", "overlay"u8.ToArray());
        Assert.True(editor.Exists("init.magisk.rc"));
        Assert.True(editor.Remove("overlay.rc"));
        Assert.False(editor.Exists("overlay.rc"));
    }
}