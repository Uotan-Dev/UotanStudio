using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo;
using UotanStudio.Firmware.SharedKernel;
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
        var path = Path.Combine(root, "kernel");
        try
        {
            using var ms = new MemoryStream();
            ms.Write([0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00]);
            ms.Write(new byte[64]);
            ms.Write(Encoding.ASCII.GetBytes("this tail is printable ascii that must not defeat the elf exclusion"));
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

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
        var path = Path.Combine(root, "firmware.ofp");
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
        var path = Path.Combine(root, "kernel");
        try
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[0x38]);
            ms.Write([(byte)'A', (byte)'R', (byte)'M', (byte)'d']);
            ms.Write(new byte[0x20]);
            ms.Write(new byte[0x1000]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

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
        var path = Path.Combine(root, "partition.bin");
        try
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[0x100]);
            ms.Write(Encoding.ASCII.GetBytes("some random payload"));
            ms.Write(new byte[0x1000]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

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
        var path = Path.Combine(root, "payload");
        try
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("OPPOENCRYPT!"));
            ms.Write(new byte[32]);
            File.WriteAllBytes(path, ms.ToArray());

            var handler = FormatDetector.Detect(path);

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