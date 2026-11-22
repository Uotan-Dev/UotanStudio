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

    [Fact]
    public void Detect_OplusTableLayout_MapsToOplusSplash()
    {
        var header = BuildOplusHeader([(0, 4, 3, 1, "fhd_test_result.raw", "fhd_test")]);
        Assert.Equal(LogoPartitionKind.OplusSplash, LogoPartitionFormat.Detect(header));
        Assert.True(LogoPartitionFormat.IsLogoPartitionHeader(header));

        var qualcomm = HeaderWith("SPLASH!!"u8, 1024);
        Assert.Equal(LogoPartitionKind.QualcommSplash, LogoPartitionFormat.Detect(qualcomm));
    }

    [Fact]
    public void Read_OplusSplash_DecodesRowPlanarRle8FrameToBmp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-oplus-splash-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "LOGO.img");
        try
        {
            Directory.CreateDirectory(directory);
            const int width = 6;
            const int height = 2;
            var frame = BuildOplusFrame(width, height, "fhd_test_result.raw", "fhd_test", bgr =>
            {
                bgr[0] = 0x10;
                bgr[1] = 0x20;
                bgr[2] = 0x30;
                bgr[3] = 0x40;
                bgr[4] = 0x50;
                bgr[5] = 0x60;
            });
            var image = new byte[0x2000 + frame.Length];
            BuildOplusHeader([(0x2000, width, height, 1, "fhd_test_result.raw", "fhd_test")])
                .AsSpan(0, 0x2000).CopyTo(image);
            frame.CopyTo(image.AsSpan(0x2000));
            File.WriteAllBytes(source, image);

            var parsed = LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken);
            Assert.Equal(LogoPartitionKind.OplusSplash, parsed.Kind);
            var entry = Assert.Single(parsed.Entries);
            Assert.Equal(LogoEntryEncoding.PlanarRle8, entry.Encoding);
            Assert.Equal(width, entry.Width);
            Assert.Equal(height, entry.Height);
            Assert.Equal("fhd_test_result.raw", parsed.Metadata["Frame0.Resource"]);
            Assert.Equal("fhd_test", parsed.Metadata["Frame0.Panel"]);

            using var input = File.OpenRead(source);
            var bytes = LogoPartitionFormat.ReadEntryImage(input, parsed, entry,
                TestContext.Current.CancellationToken);
            Assert.Equal("BM", Encoding.ASCII.GetString(bytes, 0, 2));
            Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4)));
            Assert.Equal(height, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4)));
            Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)));
            var rowSize = (width * 3 + 3) / 4 * 4;
            var firstRowStart = 54 + (height - 1) * rowSize;
            Assert.Equal(0x10, bytes[firstRowStart]);
            Assert.Equal(0x20, bytes[firstRowStart + 1]);
            Assert.Equal(0x30, bytes[firstRowStart + 2]);
            Assert.Equal(0x40, bytes[firstRowStart + 3]);
            Assert.Equal(0x50, bytes[firstRowStart + 4]);
            Assert.Equal(0x60, bytes[firstRowStart + 5]);
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
    public void Read_OplusFrameOutsideContainer_IsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-oplus-bad-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "LOGO.img");
        try
        {
            Directory.CreateDirectory(directory);
            var image = new byte[0x2000];
            BuildOplusHeader([(0x3000, 6, 2, 1, "fhd_test_result.raw", "fhd_test")])
                .AsSpan(0, 0x2000).CopyTo(image);
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
    public void Read_XiaomiBlockTable512_DecodesPngAndBmpEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-logo-xiaomi-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "logo.img");
        try
        {
            Directory.CreateDirectory(directory);
            var bmp = BuildBmp(2, 2);
            var image = new byte[33 * 512 + bmp.Length];
            "LOGO!!!!"u8.CopyTo(image.AsSpan(0x4000));
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4008), 33);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x400C), 1);
            bmp.CopyTo(image.AsSpan(33 * 512));
            File.WriteAllBytes(source, image);

            var parsed = LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken);
            Assert.Equal(LogoPartitionKind.LogoBlockTable, parsed.Kind);
            Assert.Equal("512", parsed.Metadata["BlockSize"]);
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
    public void Write_ReplaceAndAdd_PreservesContainerAndRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-logo-write-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "splash.img");
        var replacement = Path.Combine(directory, "replacement.bmp");
        try
        {
            Directory.CreateDirectory(directory);
            var bmp = BuildBmp(2, 2);
            File.WriteAllBytes(replacement, bmp);
            var frame = new byte[2048];
            "SPLASH!!"u8.CopyTo(frame.AsSpan(1024));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1032), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1036), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1040), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1044), 1);
            File.WriteAllBytes(source, frame);

            LogoPartitionWriter.ReplaceEntry(source, 0, replacement, TestContext.Current.CancellationToken);
            var replaced = LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken);
            Assert.Single(replaced.Entries);
            using (var input = File.OpenRead(source))
            {
                var decoded = LogoPartitionFormat.ReadEntryImage(input, replaced, replaced.Entries[0],
                    TestContext.Current.CancellationToken);
                Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(18, 4)));
            }

            var added = LogoPartitionWriter.AddEntry(source, replacement, TestContext.Current.CancellationToken);
            Assert.Equal(1, added);
            var after = LogoPartitionFormat.Read(source, TestContext.Current.CancellationToken);
            Assert.Equal(2, after.Entries.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Builds an Oplus splash main header (0x2000 bytes) whose frame offset table lists the given
    /// frame offsets and whose leading geometry fields mirror the first frame.
    /// <para>构建 Oplus splash 主头部（0x2000 字节），帧偏移表列出给定帧偏移，前部几何字段与首帧一致。</para>
    /// </summary>
    private static byte[] BuildOplusHeader((uint Offset, int Width, int Height, uint Type, string Resource, string Panel)[] frames)
    {
        var header = new byte[0x2000];
        "SPLASH!!"u8.CopyTo(header);
        var first = frames[0];
        WriteOplusFrameHeaderFields(header, first.Width, first.Height,
            (uint)((0x1000 - 0x200 + first.Width * first.Height * 3 + 255) / 512 * 512), first.Type);
        Encoding.ASCII.GetBytes(first.Resource).CopyTo(header.AsSpan(0x180));
        Encoding.ASCII.GetBytes(first.Panel).CopyTo(header.AsSpan(0x1C0));
        for (var i = 0; i < frames.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30 + (i * 4)), frames[i].Offset);
        }

        foreach (var (offset, width, height, type, resource, panel) in frames)
        {
            if (offset == 0 || offset + 0x200 > (uint)header.Length)
            {
                continue;
            }

            WriteOplusFrameHeaderFields(header.AsSpan((int)offset, 0x200), width, height,
                (uint)((0x1000 - 0x200 + width * height * 3 + 255) / 512 * 512), type);
            Encoding.ASCII.GetBytes(resource).CopyTo(header.AsSpan((int)offset + 0x180));
            Encoding.ASCII.GetBytes(panel).CopyTo(header.AsSpan((int)offset + 0x1C0));
        }

        return header;
    }

    /// <summary>
    /// Writes the Oplus frame header geometry fields (magic, width, height, payload bytes, type).
    /// <para>写入 Oplus 帧头的几何字段（魔数、宽、高、负载字节数、类型）。</para>
    /// </summary>
    private static void WriteOplusFrameHeaderFields(Span<byte> destination, int width, int height,
        uint payloadBytes, uint type)
    {
        "SPLASH!!"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x20..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x24..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x28..], payloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x2C..], type);
    }

    /// <summary>
    /// Builds one full Oplus frame (0x1000-byte header plus a row-planar RLE8 payload) whose
    /// first pixel row carries per-column values from <paramref name="firstRowValues"/>. The
    /// declared payload size equals the actual payload byte length.
    /// <para>构建一个完整的 Oplus 帧（0x1000 字节帧头加逐行平面 RLE8 负载），首行各列取自
    /// <paramref name="firstRowValues"/>。声明的负载大小等于实际负载字节长度。</para>
    /// </summary>
    private static byte[] BuildOplusFrame(int width, int height, string resource, string panel,
        Action<byte[]> firstRowValues)
    {
        var header = new byte[0x1000];
        Encoding.ASCII.GetBytes(resource).CopyTo(header.AsSpan(0x180));
        Encoding.ASCII.GetBytes(panel).CopyTo(header.AsSpan(0x1C0));

        var bgr = new byte[width * height * 3];
        firstRowValues(bgr);
        // The on-disk stream stores each pixel as interleaved R, G, B components, each emitted
        // as a (value, count=1) run.
        var payload = new List<byte>();
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            payload.Add(bgr[pixel * 3 + 2]); // R
            payload.Add(1);
            payload.Add(bgr[pixel * 3 + 1]); // G
            payload.Add(1);
            payload.Add(bgr[pixel * 3]);     // B
            payload.Add(1);
        }

        WriteOplusFrameHeaderFields(header, width, height, (uint)payload.Count, 1);
        var result = new byte[0x1000 + payload.Count];
        header.CopyTo(result, 0);
        ((ICollection<byte>)payload).ToArray().CopyTo(result, 0x1000);
        return result;
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