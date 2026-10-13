using System.Text;
using UotanStudio.Firmware.Core.Magic;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Identification tests for the compiled magic engine (upstream file(1) magic(5) corpus emitted
/// into C# rule tables by UotanStudio.Firmware.MagicGen). Every expected description below was
/// captured from the engine and cross-checked against the installed <c>file(1)</c> 5.48 oracle.
/// The vendored corpus tracks upstream master, so a few trailing description clauses that require a
/// full-file read (ELF "no section header", gzip ISIZE) or that classify plain text are intentionally
/// out of scope for a header-probe identifier and are asserted only on their stable prefix.
/// <para>编译型魔数引擎 FileMagic 的识别测试。</para>
/// </summary>
public class FileMagicTests
{
    private static byte[] Png =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xDE,
    ];

    private static byte[] Gzip => [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03];
    private static byte[] Xz => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static byte[] Zstd => [0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x05];
    private static byte[] Bzip2 => [0x42, 0x5A, 0x68, 0x31, 0x31, 0x26];
    private static byte[] Lz4Frame => [0x04, 0x22, 0x4D, 0x18, 0x64, 0x70];
    private static byte[] Lz4Legacy => [0x02, 0x21, 0x4C, 0x18, 0x00, 0x00];
    private static byte[] Lzma => [0x5D, 0x00, 0x00, 0x80, 0x00];
    private static byte[] Fdt => [0xD0, 0x0D, 0xFE, 0xED, 0x00, 0x00];
    private static byte[] Sparse => [0x3A, 0xFF, 0x26, 0xED, 0x10, 0x00];

    private static byte[] EmptyZip =>
    [
        0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Identify_Png_ReturnsExactDescription()
    {
        var info = FileMagic.Identify(Png);
        Assert.NotNull(info);
        Assert.Equal("PNG image data, 1 x 1, 8-bit/color RGB, non-interlaced", info!.Description);
        Assert.Equal("image/png", info.MimeType);
        Assert.Equal("png", info.Extensions);
    }

    [Fact]
    public void Identify_Gzip_ReturnsDescriptionAndMime()
    {
        var info = FileMagic.Identify(Gzip);
        Assert.Equal("gzip compressed data, from Unix", info?.Description);
        Assert.Equal("application/gzip", info?.MimeType);
    }

    [Fact]
    public void Identify_Xz_ReturnsDescriptionAndExtension()
    {
        var info = FileMagic.Identify(Xz);
        Assert.Equal("XZ compressed data, checksum", info?.Description);
        Assert.Equal("application/x-xz", info?.MimeType);
        Assert.Equal("xz", info?.Extensions);
    }

    [Fact]
    public void Identify_Zstd_ReturnsDescription()
    {
        var info = FileMagic.Identify(Zstd);
        Assert.Equal("Zstandard compressed data (v0.8+), Dictionary ID: None", info?.Description);
        Assert.Equal("application/zstd", info?.MimeType);
        Assert.Equal("zst", info?.Extensions);
    }

    [Fact]
    public void Identify_Bzip2_ReturnsDescription()
    {
        var info = FileMagic.Identify(Bzip2);
        Assert.Equal("bzip2 compressed data, block size = 100k", info?.Description);
        Assert.Equal("application/x-bzip2", info?.MimeType);
        Assert.Equal("bz2", info?.Extensions);
    }

    [Fact]
    public void Identify_Lz4Frame_ReturnsDescription()
    {
        var info = FileMagic.Identify(Lz4Frame);
        Assert.Equal("LZ4 compressed data (v1.4+)", info?.Description);
        Assert.Equal("application/x-lz4", info?.MimeType);
        Assert.Equal("lz4", info?.Extensions);
    }

    [Fact]
    public void Identify_Lz4Legacy_ReturnsDescription()
    {
        Assert.Equal("LZ4 compressed data (v0.1-v0.9)", FileMagic.Describe(Lz4Legacy));
    }

    [Fact]
    public void Identify_EmptyZip_ReturnsArchiveDescription()
    {
        var info = FileMagic.Identify(EmptyZip);
        Assert.Equal("Zip archive data (empty)", info?.Description);
        Assert.Equal("application/zip", info?.MimeType);
    }

    [Fact]
    public void Identify_TruncatedZipHeader_ReturnsNull()
    {
        Assert.Null(FileMagic.Identify([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]));
    }

    [Fact]
    public void Identify_BareLzmaHeader_ReturnsNull()
    {
        Assert.Null(FileMagic.Identify(Lzma));
    }

    [Fact]
    public void Identify_Elf64Aarch64Executable()
    {
        var info = FileMagic.Identify(BuildElf(elf64: true, machine: 0xB7, type: 2));
        Assert.Equal("ELF 64-bit LSB executable, ARM aarch64, version 1 (SYSV)", info?.Description);
        Assert.Equal("application/x-executable", info?.MimeType);
    }

    [Fact]
    public void Identify_Elf64X86Executable()
    {
        Assert.Equal("ELF 64-bit LSB executable, x86-64, version 1 (SYSV)", FileMagic.Describe(BuildElf(elf64: true, machine: 0x3E, type: 2)));
    }

    [Fact]
    public void Identify_Elf64RiscVShared()
    {
        var info = FileMagic.Identify(BuildElf(elf64: true, machine: 0xF3, type: 3));
        Assert.Equal("ELF 64-bit LSB shared object, UCB RISC-V, soft-float ABI, version 1 (SYSV)", info?.Description);
        Assert.Equal("application/x-sharedlib", info?.MimeType);
    }

    [Fact]
    public void Identify_Elf32ArmShared()
    {
        Assert.Contains("ELF 32-bit LSB shared object, ARM", FileMagic.Describe(BuildElf(elf64: false, machine: 0x28, type: 3)));
    }

    [Fact]
    public void Identify_Elf32I386Relocatable()
    {
        Assert.Contains("ELF 32-bit LSB relocatable, Intel i386", FileMagic.Describe(BuildElf(elf64: false, machine: 0x03, type: 1)));
    }

    [Fact]
    public void Identify_Fdt_ReturnsDeviceTree()
    {
        var info = FileMagic.Identify(Fdt);
        Assert.Equal("Flattened device tree blob", info?.Description);
        Assert.Equal("application/octet-stream", info?.MimeType);
    }

    [Fact]
    public void Identify_Sparse_ReturnsAndroidSparse()
    {
        Assert.Equal("Android sparse image", FileMagic.Describe(Sparse));
    }

    [Fact]
    public void Identify_CpioNewcVariants()
    {
        Assert.Equal("ASCII cpio archive (SVR4 with no CRC)", FileMagic.Describe(Ascii("070701")));
        Assert.Equal("ASCII cpio archive (SVR4 with CRC)", FileMagic.Describe(Ascii("070702")));
    }

    [Fact]
    public void Identify_AndroidBootimg()
    {
        Assert.Equal("Android bootimg", FileMagic.Describe(Ascii("ANDROID!")));
    }

    [Fact]
    public void Identify_AndroidVendorBoot()
    {
        Assert.Equal("Android vendor boot image", FileMagic.Describe(Ascii("VNDRBOOT")));
    }

    [Fact]
    public void Identify_AndroidDtboHeaderAndImage()
    {
        Assert.Equal("Android device tree overlay header", FileMagic.Describe(Ascii("DTBH")));
        Assert.Equal("Android device tree overlay image", FileMagic.Describe(Ascii("DTBO")));
    }

    [Fact]
    public void Identify_AndroidOtaPayload()
    {
        var info = FileMagic.Identify(Ascii("CrAU"));
        Assert.Equal("Android OTA payload", info?.Description);
        Assert.Equal("application/octet-stream", info?.MimeType);
    }

    [Fact]
    public void Identify_AndroidVerifiedBoot()
    {
        Assert.Equal("Android Verified Boot image", FileMagic.Describe(Ascii("AVBf")));
    }

    [Fact]
    public void Identify_RockchipFirmwareVariants()
    {
        Assert.Equal("Rockchip firmware image", FileMagic.Describe(Ascii("RKFW")));
        Assert.Equal("Rockchip firmware image", FileMagic.Describe(Ascii("RKAF")));
    }

    [Fact]
    public void Identify_LinuxKernelArm64Image()
    {
        Assert.Equal("Linux kernel ARM64 boot executable Image, little-endian", FileMagic.Describe(BuildKernelImage("ARM", 0x64)));
    }

    [Fact]
    public void Identify_LinuxKernelRiscVImage()
    {
        Assert.Equal("Linux kernel RISC-V boot executable Image, little-endian", FileMagic.Describe(BuildKernelImage("RSC", 0x05)));
    }

    [Fact]
    public void Identify_LinuxKernelArmZImage()
    {
        byte[] header = new byte[0x28];
        header[0x24] = 0x18;
        header[0x25] = 0x28;
        header[0x26] = 0x6F;
        header[0x27] = 0x01;
        Assert.Equal("Linux kernel ARM boot executable zImage", FileMagic.Describe(header));
    }

    [Fact]
    public void Identify_PlainText_ReturnsNull()
    {
        Assert.Null(FileMagic.Identify(Ascii("this is just plain text, not a magic")));
    }

    [Fact]
    public void Identify_Empty_ReturnsNull()
    {
        Assert.Null(FileMagic.Identify(Array.Empty<byte>()));
    }

    [Fact]
    public void IsElf_True_ForElfMagic() => Assert.True(FileMagic.IsElf(BuildElf(elf64: true, machine: 0xB7, type: 2)));

    [Fact]
    public void IsElf_False_ForGzip() => Assert.False(FileMagic.IsElf(Gzip));

    [Fact]
    public void MimeType_ReturnsMime_ForKnownType()
    {
        Assert.Equal("image/png", FileMagic.MimeType(Png));
    }

    [Fact]
    public void IdentifyFile_ReadsHeader_AndIdentifies()
    {
        string path = Path.Combine(Path.GetTempPath(), "uotan-filemagic-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            File.WriteAllBytes(path, Png);
            var info = FileMagic.IdentifyFile(path);
            Assert.Equal("PNG image data, 1 x 1, 8-bit/color RGB, non-interlaced", info?.Description);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IdentifyFile_NonExistent_ReturnsNull()
    {
        Assert.Null(FileMagic.IdentifyFile(Path.Combine(Path.GetTempPath(), "uotan-no-" + Guid.NewGuid().ToString("N") + ".bin")));
    }

    private static byte[] BuildElf(bool elf64, ushort machine, ushort type)
    {
        byte[] header = new byte[64];
        header[0] = 0x7F;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = elf64 ? (byte)2 : (byte)1;
        header[5] = 1;
        header[6] = 1;
        header[16] = (byte)(type & 0xFF);
        header[17] = (byte)(type >> 8);
        header[18] = (byte)(machine & 0xFF);
        header[19] = (byte)(machine >> 8);
        header[20] = 1;
        return header;
    }

    private static byte[] BuildKernelImage(string threeAscii, byte fourth)
    {
        byte[] header = new byte[0x40];
        header[0x38] = (byte)threeAscii[0];
        header[0x39] = (byte)threeAscii[1];
        header[0x3A] = (byte)threeAscii[2];
        header[0x3B] = fourth;
        return header;
    }
}

/// <summary>
/// Differential parity checks against the system <c>file(1)</c>. These exercise the same fixtures
/// through both the compiled engine and the upstream oracle and assert both classify the file into
/// the same family (matched on a shared keyword rather than the full description, because the vendored
/// corpus tracks upstream master and some trailing clauses drift). Tests skip when no <c>file</c>
/// executable is available on <c>PATH</c>; the oracle comparison for these fixtures was performed
/// manually with file(1) 5.48 while the assertions above were authored.
/// <para>与系统 <c>file(1)</c> 的差分一致性测试。</para>
/// </summary>
public class FileMagicDifferentialTests
{
    [Theory]
    [InlineData("PNG", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xDE })]
    [InlineData("XZ", new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 })]
    [InlineData("gzip", new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03 })]
    [InlineData("Zstandard", new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x05 })]
    [InlineData("bzip2", new byte[] { 0x42, 0x5A, 0x68, 0x31, 0x31, 0x26 })]
    [InlineData("cpio", new byte[] { 0x30, 0x37, 0x30, 0x37, 0x30, 0x31 })]
    public void Engine_And_FileOracle_AgreeOnFamily(string token, byte[] data)
    {
        string? oracle = RunFileOracle(data);
        if (oracle is null)
        {
            Assert.Skip("No 'file' executable on PATH; differential parity requires the file(1) oracle.");
        }

        string? engine = FileMagic.Describe(data);
        Assert.NotNull(engine);
        Assert.Contains(token, engine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(token, oracle!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Engine_And_FileOracle_AgreeOnElfArchitecture()
    {
        byte[] elf = new byte[64];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        elf[17] = 2;
        elf[18] = 0xB7;
        elf[20] = 1;

        string? oracle = RunFileOracle(elf);
        if (oracle is null)
        {
            Assert.Skip("No 'file' executable on PATH; differential parity requires the file(1) oracle.");
        }

        Assert.Contains("ELF", FileMagic.Describe(elf));
        Assert.Contains("ELF", oracle!);
    }

    private static string? RunFileOracle(byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), "uotan-magicdiff-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        try
        {
            File.WriteAllBytes(path, data);
            var psi = new System.Diagnostics.ProcessStartInfo("file", "--brief " + QuoteArg(path))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string QuoteArg(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}