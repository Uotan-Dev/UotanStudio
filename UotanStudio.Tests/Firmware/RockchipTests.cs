using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Rockchip;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Format-level tests for the Rockchip RKAF/RKFW handler, built around minimal synthetic images
/// (header + fake FDT data + fake image data) with consistent magic/length/offset fields.
/// <para>Rockchip RKAF/RKFW 处理器的格式级测试，基于测试内合成的最小镜像（头部 + 假 FDT 数据 +
/// 假镜像数据，魔数/长度/偏移一致）。</para>
/// </summary>
public class RockchipTests
{
    private const string RkfwFormatName = "Rockchip RKAF/RKFW Firmware";

    /// <summary>
    /// Builds a minimal Rockchip RKAF/RKFW image: 540-byte header (magic tag + consistent
    /// fdt/image offset/size fields) followed by the FDT payload and the image payload.
    /// <para>合成最小 Rockchip RKAF/RKFW 镜像：540 字节头部（魔数 + 一致的 fdt/image 偏移/大小字段）
    /// 后接 FDT 数据与镜像数据。</para>
    /// </summary>
    internal static byte[] BuildRkImage(uint tag, byte[] fdt, byte[] image)
    {
        var fdtOffset = RkFwHeader.DefaultHeaderSize;
        var head = new RkFwHeader
        {
            Tag = tag,
            Version = 1,
            HeaderSize = RkFwHeader.DefaultHeaderSize,
            FdtOffset = fdtOffset,
            FdtSize = (uint)fdt.Length,
            ImageOffset = fdtOffset + (uint)fdt.Length,
            ImageSize = (uint)image.Length,
        };

        using var ms = new MemoryStream();
        var headBytes = head.ToBytes();
        Assert.Equal(RkFwHeader.DefaultHeaderSize, (uint)headBytes.Length);
        ms.Write(headBytes);
        ms.Write(fdt);
        ms.Write(image);
        return ms.ToArray();
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-rockchip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Detect_RkfwExtension_IsRockchip()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.rkfw");
        try
        {
            File.WriteAllBytes(path, BuildRkImage(RkFwHeader.MagicRkfw, [0x01, 0x02, 0x03], [0xAA, 0xBB]));
            var handler = FormatDetector.Detect(path);
            Assert.Equal(RkfwFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_NoExtension_ContentMagic_IsRockchip()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware");
        try
        {
            File.WriteAllBytes(path, BuildRkImage(RkFwHeader.MagicRkaf, [0x0A], [0x11, 0x22, 0x33]));
            var handler = FormatDetector.Detect(path);
            Assert.Equal(RkfwFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_ReportsFdtAndImageEntries()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.rkfw");
        try
        {
            File.WriteAllBytes(path, BuildRkImage(RkFwHeader.MagicRkfw, [0x0A, 0x0B], [0x11, 0x22]));

            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken);
            Assert.True(result.Success);
            Assert.Contains("2 entries", result.Summary, StringComparison.Ordinal);
            Assert.Equal("2", result.Metadata?["FileCount"]);
            Assert.Contains("fdt.dtb", result.Entries);
            Assert.Contains("image.bin", result.Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_ExtractsFdtAndImageByteExact()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.rkfw");
        var outDir = Path.Combine(root, "out");
        try
        {
            var fdt = Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray();
            var image = Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(path, BuildRkImage(RkFwHeader.MagicRkfw, fdt, image));

            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.UnpackAsync(path, outDir, null, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);

            Assert.Equal(fdt, await File.ReadAllBytesAsync(Path.Combine(outDir, "fdt.dtb")));
            Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(outDir, "image.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_BadMagic_IsNotRockchip()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware");
        try
        {
            var bytes = BuildRkImage(RkFwHeader.MagicRkfw, [0x01], [0x02]);
            bytes[0] = 0x00; // 破坏魔数 "Rkfw"。 Corrupt the "Rkfw" magic.
            File.WriteAllBytes(path, bytes);
            var handler = FormatDetector.Detect(path);
            Assert.NotEqual(RkfwFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_TruncatedFile_IsNotRockchip()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware");
        try
        {
            // 仅含 4 字节魔数、长度不足头部结构大小。
            // Only the 4-byte magic; too short for the header struct.
            File.WriteAllBytes(path, [0x52, 0x6B, 0x66, 0x77]);
            var handler = FormatDetector.Detect(path);
            Assert.NotEqual(RkfwFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_BadMagic_ThrowsInvalidData()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware");
        var outDir = Path.Combine(root, "out");
        try
        {
            var bytes = BuildRkImage(RkFwHeader.MagicRkaf, [0x01], [0x02]);
            bytes[0] = 0xFF;
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => RockchipExtractor.Extract(path, outDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_TruncatedImage_FailsCleanly()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.rkfw");
        var outDir = Path.Combine(root, "out");
        try
        {
            var bytes = BuildRkImage(RkFwHeader.MagicRkfw, [0x01, 0x02, 0x03], [0x11, 0x22, 0x33]);
            File.WriteAllBytes(path, bytes[..(bytes.Length - 1)]);

            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.UnpackAsync(path, outDir, null, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pack_ReturnsFail()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.rkfw");
        var outPath = Path.Combine(root, "packed.rkfw");
        try
        {
            File.WriteAllBytes(path, BuildRkImage(RkFwHeader.MagicRkfw, [0x01], [0x02]));
            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.PackAsync(path, outPath, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}