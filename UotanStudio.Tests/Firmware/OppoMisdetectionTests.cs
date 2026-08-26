using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Oppo;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Regression tests for the OFP/MTK misidentification fix: a bare ELF kernel (even with a
/// printable-ASCII tail) must NOT be routed to the Oppo handler, while a genuine .ofp still is.
/// <para>OFP/MTK 误识别修复回归测试：裸 ELF 内核（即使末尾为可打印 ASCII）不得被归入 Oppo 处理器，
/// 而真正的 .ofp 仍能被正确识别。</para>
/// </summary>
public class OppoMisdetectionTests
{
    private const string OppoFormatName = "Oppo Firmware (OFP/OPS/OZIP)";

    [Fact]
    public void Detect_BareElfWithPrintableTail_NotOppo()
    {
        FirmwareFormatRegistry.RegisterAll();
        OppoInitializer.EnsureInitialized();

        var root = CreateTempDir();
        var path = Path.Combine(root, "kernel"); // 无扩展名 → 走内容检测分支。
        try
        {
            using var ms = new MemoryStream();
            ms.Write([0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00]);
            ms.Write(new byte[64]);
            ms.Write(Encoding.ASCII.GetBytes("this tail is printable ascii that must not defeat the elf exclusion"));
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

            // 裸 ELF 不得被识别为 OFP MTK。
            // A bare ELF kernel must not be identified as OFP MTK.
            Assert.NotEqual(OppoFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_OfpWithMagic_IsOppo()
    {
        FirmwareFormatRegistry.RegisterAll();
        OppoInitializer.EnsureInitialized();

        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.ofp"); // 强扩展名 + OZip 魔数。
        try
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("OPPOENCRYPT!"));
            ms.Write(new byte[32]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

            Assert.NotNull(handler);
            Assert.Equal(OppoFormatName, handler!.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_Arm64ImageWithZeroTail_NotOppo()
    {
        FirmwareFormatRegistry.RegisterAll();
        OppoInitializer.EnsureInitialized();

        var root = CreateTempDir();
        var path = Path.Combine(root, "kernel"); // 无扩展名 → 走内容检测分支。
        try
        {
            using var ms = new MemoryStream();
            // ARM64 内核镜像头：偏移 0x38 处为魔数 "ARM\x64"。
            // ARM64 kernel Image header with the "ARM\x64" magic at offset 0x38.
            ms.Write(new byte[0x38]);
            ms.Write([(byte)'A', (byte)'R', (byte)'M', (byte)'d']);
            ms.Write(new byte[0x20]);
            // 尾部全零填充：解混淆后恰为密钥重复串，此前会被误判为 OFP MTK。
            // Fully zero-padded tail: de-obfuscates to the repeated shuffle key, previously misdetected as OFP MTK.
            ms.Write(new byte[0x1000]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

            // ARM64 内核镜像不得被识别为 OFP MTK。
            // An ARM64 kernel Image must not be identified as OFP MTK.
            Assert.NotEqual(OppoFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_ZeroPaddedTail_NotOppo()
    {
        FirmwareFormatRegistry.RegisterAll();
        OppoInitializer.EnsureInitialized();

        var root = CreateTempDir();
        var path = Path.Combine(root, "partition.bin"); // 泛 .bin → 走内容确认分支。
        try
        {
            using var ms = new MemoryStream();
            // 无任何魔数的普通二进制，末尾为全零填充（如原始分区镜像）。
            // A plain binary with no magic and a fully zero-padded tail (e.g. a raw partition image).
            ms.Write(new byte[0x100]);
            ms.Write(Encoding.ASCII.GetBytes("some random payload"));
            ms.Write(new byte[0x1000]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

            // 零填充尾部不得因解混淆成密钥串而被误判为 OFP MTK；泛 .bin 也须内容确认。
            // A zero-padded tail must not be misidentified as OFP MTK via the de-shuffled key string;
            // a generic .bin still requires content confirmation.
            Assert.NotEqual(OppoFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_OppoContentWithoutSuffix_NotOppo()
    {
        FirmwareFormatRegistry.RegisterAll();
        OppoInitializer.EnsureInitialized();

        var root = CreateTempDir();
        var path = Path.Combine(root, "payload"); // 无 Oppo 后缀。
        try
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("OPPOENCRYPT!")); // 真实 OZip 魔数。
            ms.Write(new byte[32]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

            // 即使内容命中 OZip 魔数，无 Oppo 后缀也不得识别为 OFP。
            // Even with genuine OZip magic, a file without an Oppo suffix must not be Oppo.
            Assert.NotEqual(OppoFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "UotanStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}