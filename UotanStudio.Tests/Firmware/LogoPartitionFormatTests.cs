using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.LogoPartition;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.UiAbstractions;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class LogoPartitionFormatTests
{
    [Fact]
    public void Detect_KnownMagics_MapsToContainerFamilies()
    {
        var logo = HeaderWith("LOGO!!!!"u8, 0x4000);
        Assert.Equal(LogoPartitionKind.LogoBlockTable, LogoPartitionFormat.Detect(logo));
        Assert.True(LogoPartitionFormat.IsLogoPartitionHeader(logo));

        var splash = HeaderWith("SPLASH!!"u8, 1024);
        Assert.Equal(LogoPartitionKind.QualcommSplash, LogoPartitionFormat.Detect(splash));
        Assert.True(LogoPartitionFormat.IsLogoPartitionHeader(splash));

        var oppo = HeaderWith("SPLASH LOGO!"u8, 0x4000);
        Assert.Equal(LogoPartitionKind.Unknown, LogoPartitionFormat.Detect(oppo));
        Assert.False(LogoPartitionFormat.IsLogoPartitionHeader(oppo));

        var guoke = HeaderWith("###"u8, 0);
        "LOGO_TABLE"u8.CopyTo(guoke.AsSpan(8));
        Assert.Equal(LogoPartitionKind.GuoKeLogo, LogoPartitionFormat.Detect(guoke));

        Assert.Equal(LogoPartitionKind.Unknown, LogoPartitionFormat.Detect(new byte[64 * 1024]));
    }

    [Fact]
    public void Read_LogoBlockTable_ParsesBmpEntriesAndExtractsStandardImages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-logo-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "logo.img");
        var output = Path.Combine(directory, "out");
        try
        {
            Directory.CreateDirectory(directory);
            var bmp = BuildBmp(2, 2);
            var image = new byte[0x7000];
            "LOGO!!!!"u8.CopyTo(image.AsSpan(0x4000));
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4008), 5);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x400C), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4010), 6);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4014), 1);
            bmp.CopyTo(image.AsSpan(0x5000));
            bmp.CopyTo(image.AsSpan(0x6000));
            File.WriteAllBytes(source, image);

            var parsed = LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken);
            Assert.Equal(LogoPartitionKind.LogoBlockTable, parsed.Kind);
            Assert.Equal(0x4000, parsed.HeaderOffset);
            Assert.Equal(2, parsed.Entries.Count);
            Assert.Equal("logo_00.bmp", parsed.Entries[0].FileName);
            Assert.Equal("2 x 2", $"{parsed.Entries[0].Width} x {parsed.Entries[0].Height}");
            Assert.Equal(24, parsed.Entries[0].BitsPerPixel);

            using (var input = File.OpenRead(source))
            {
                var bytes = LogoPartitionFormat.ReadEntryImage(input, parsed, parsed.Entries[0],
                    TestContext.Current.CancellationToken);
                Assert.Equal("BM", Encoding.ASCII.GetString(bytes, 0, 2));
                Assert.Equal(bmp.Length, bytes.Length);
            }

            var written = LogoPartitionFormat.Extract(source, output,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(["logo_00.bmp", "logo_01.bmp"], written);
            Assert.All(written, name => Assert.Equal(bmp.Length, new FileInfo(Path.Combine(output, name)).Length));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Read_QualcommSplash_DecodesRle24FrameToBmp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-splash-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "splash.img");
        try
        {
            Directory.CreateDirectory(directory);
            var frame = new byte[2048];
            "SPLASH!!"u8.CopyTo(frame.AsSpan(1024));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1032), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1036), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1040), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1044), 1);
            frame[1536] = 131;
            frame[1537] = 0x11;
            frame[1538] = 0x22;
            frame[1539] = 0x33;
            File.WriteAllBytes(source, frame);

            var parsed = LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken);
            Assert.Equal(LogoPartitionKind.QualcommSplash, parsed.Kind);
            Assert.Equal(1024, parsed.HeaderOffset);
            var entry = Assert.Single(parsed.Entries);
            Assert.Equal(LogoEntryEncoding.Rle24Bgr, entry.Encoding);
            Assert.Equal("splash_01.bmp", entry.FileName);

            using var input = File.OpenRead(source);
            var bytes = LogoPartitionFormat.ReadEntryImage(input, parsed, entry,
                TestContext.Current.CancellationToken);
            Assert.Equal("BM", Encoding.ASCII.GetString(bytes, 0, 2));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4)));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4)));
            Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Read_OutOfRangeBlock_IsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-logo-bad-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "logo.img");
        try
        {
            Directory.CreateDirectory(directory);
            var image = new byte[0x5000];
            "LOGO!!!!"u8.CopyTo(image.AsSpan(0x4000));
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4008), 0xFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x400C), 1);
            File.WriteAllBytes(source, image);

            Assert.Throws<InvalidDataException>(() =>
                LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveMode_MagicHeader_RoutesToLogoPartition()
    {
        var header = HeaderWith("LOGO!!!!"u8, 0x4000);
        Assert.Equal(EditorOpenMode.LogoPartition,
            DocumentOpenRegistry.ResolveMode("logo.bin", header, null));

        var picture = HeaderWith("SPLASH!!"u8, 1024);
        Assert.Equal(EditorOpenMode.LogoPartition,
            DocumentOpenRegistry.ResolveMode("splash.img", picture, null));
    }

    private static byte[] HeaderWith(ReadOnlySpan<byte> magic, int offset)
    {
        var header = new byte[64 * 1024];
        magic.CopyTo(header.AsSpan(offset));
        return header;
    }

    private static byte[] BuildBmp(int width, int height)
    {
        var rowSize = (width * 3 + 3) / 4 * 4;
        var result = new byte[54 + rowSize * height];
        "BM"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 24);
        return result;
    }
}