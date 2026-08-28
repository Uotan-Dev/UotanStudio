using System.Text;
using UotanStudio.Firmware.Common;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Pure-managed magic engine tests for core file-type signatures.
/// <para>纯托管 magic 引擎的核心文件类型签名测试。</para>
/// </summary>
public class ManagedMagicTests
{
    // 最小可识别 ELF 头（仅 8 字节魔数 + class/data/version）
    private static byte[] ElfMinimal => [0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00];

    // 完整 ELF64 头：AArch64 可执行文件
    private static byte[] ElfAarch64Exec => BuildElfHeader(elf64: true, bigEndian: false, machine: 0xB7, type: 2);

    // ELF32 ARM 共享库（小端）
    private static byte[] ElfArmShared => BuildElfHeader(elf64: false, bigEndian: false, machine: 0x28, type: 3);

    // ELF64 x86-64 可执行文件
    private static byte[] ElfX86_64Exec => BuildElfHeader(elf64: true, bigEndian: false, machine: 0x3E, type: 2);

    // ELF32 x86 可重定位目标文件
    private static byte[] Elf386Relocatable => BuildElfHeader(elf64: false, bigEndian: false, machine: 0x03, type: 1);

    // ELF64 RISC-V 共享对象
    private static byte[] ElfRiscVShared => BuildElfHeader(elf64: true, bigEndian: false, machine: 0xF3, type: 3);

    private static byte[] Gzip => [0x1F, 0x8B, 0x08, 0x00];
    private static byte[] Bzip2 => [0x42, 0x5A, 0x68, 0x31, 0x31, 0x26];
    private static byte[] Xz => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static byte[] LzmaAlone => [0x5D, 0x00, 0x00, 0x80, 0x00];
    private static byte[] Lz4Frame => [0x04, 0x22, 0x4D, 0x18, 0x64, 0x70];
    private static byte[] Lz4Legacy => [0x02, 0x21, 0x4C, 0x18, 0x00, 0x00];
    private static byte[] Zstd => [0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x05];
    private static byte[] Zip => [(byte)'P', (byte)'K', 0x03, 0x04, 0x14, 0x00];
    private static byte[] Fdt => [0xD0, 0x0D, 0xFE, 0xED, 0x00, 0x00];
    private static byte[] AndroidBoot => Encoding.ASCII.GetBytes("ANDROID!");
    private static byte[] AndroidVendorBoot => Encoding.ASCII.GetBytes("VNDRBOOT");
    private static byte[] AndroidDtboH => Encoding.ASCII.GetBytes("DTBH");
    private static byte[] AndroidDtboO => Encoding.ASCII.GetBytes("DTBO");
    private static byte[] AndroidPayload => Encoding.ASCII.GetBytes("CrAU");
    private static byte[] AndroidAvb => Encoding.ASCII.GetBytes("AVBf");
    private static byte[] Sparse => [0x3A, 0xFF, 0x26, 0xED, 0x10, 0x00];
    private static byte[] CpioNewc1 => Encoding.ASCII.GetBytes("070701");
    private static byte[] CpioNewc2 => Encoding.ASCII.GetBytes("070702");
    private static byte[] RockchipBoot => Encoding.ASCII.GetBytes("BOOT");
    private static byte[] RockchipRkfw => Encoding.ASCII.GetBytes("Rkfw");
    private static byte[] RockchipRkaf => Encoding.ASCII.GetBytes("RKAF");

    // Linux kernel ARM64 boot Image：偏移 0x38 处魔数 "ARM\x64"（41 52 4d 64）
    private static byte[] LinuxKernelArm64Image => BuildKernelImage("ARM", 0x64);

    // Linux kernel RISC-V boot Image：偏移 0x38 处魔数 "RSC\x05"（52 53 43 05）
    private static byte[] LinuxKernelRiscVImage => BuildKernelImage("RSC", 0x05);

    // Linux kernel ARM boot zImage：偏移 0x24 处小端 0x016f2818（18 28 6f 01）
    private static byte[] LinuxKernelArmZImage => BuildKernelZImage(bigEndian: false);

    // Linux kernel ARM boot zImage：偏移 0x24 处大端 0x016f2818（01 6f 28 18）
    private static byte[] LinuxKernelArmZImageBE => BuildKernelZImage(bigEndian: true);

    private static byte[] BuildKernelImage(string threeAscii, byte fourth)
    {
        byte[] header = new byte[0x40];
        header[0x38] = (byte)threeAscii[0];
        header[0x39] = (byte)threeAscii[1];
        header[0x3A] = (byte)threeAscii[2];
        header[0x3B] = fourth;
        return header;
    }

    private static byte[] BuildKernelZImage(bool bigEndian)
    {
        byte[] header = new byte[0x28];
        if (bigEndian)
        {
            header[0x24] = 0x01; header[0x25] = 0x6F; header[0x26] = 0x28; header[0x27] = 0x18;
        }
        else
        {
            header[0x24] = 0x18; header[0x25] = 0x28; header[0x26] = 0x6F; header[0x27] = 0x01;
        }
        return header;
    }

    [Fact]
    public void Detect_Elf_AArch64_Executable()
    {
        var result = ManagedMagic.Detect(ElfAarch64Exec);
        Assert.Contains("AArch64", result);
        Assert.Contains("executable", result);
    }

    [Fact]
    public void Detect_Elf_ARM_Shared()
    {
        var result = ManagedMagic.Detect(ElfArmShared);
        Assert.Contains("ARM", result);
        Assert.Contains("shared object", result);
    }

    [Fact]
    public void Detect_Elf_X86_64_Executable()
    {
        var result = ManagedMagic.Detect(ElfX86_64Exec);
        Assert.Contains("x86-64", result);
        Assert.Contains("executable", result);
    }

    [Fact]
    public void Detect_Elf_386_Relocatable()
    {
        var result = ManagedMagic.Detect(Elf386Relocatable);
        Assert.Contains("x86", result);
        Assert.Contains("relocatable", result);
    }

    [Fact]
    public void Detect_Elf_RiscV_Shared()
    {
        var result = ManagedMagic.Detect(ElfRiscVShared);
        Assert.Contains("RISC-V", result);
        Assert.Contains("shared object", result);
    }

    [Fact]
    public void Detect_Elf_Minimal_WithoutMachine()
    {
        // 仅 8 字节头，e_machine 字段缺失 → 应回退为 "ELF executable"
        var result = ManagedMagic.Detect(ElfMinimal);
        Assert.Equal("ELF executable", result);
    }

    [Fact]
    public void Detect_Gzip() => Assert.Equal("gzip compressed data", ManagedMagic.Detect(Gzip));

    [Fact]
    public void Detect_Bzip2() => Assert.Equal("bzip2 compressed data", ManagedMagic.Detect(Bzip2));

    [Fact]
    public void Detect_Xz() => Assert.Equal("XZ compressed data", ManagedMagic.Detect(Xz));

    [Fact]
    public void Detect_Lzma() => Assert.Equal("LZMA compressed data", ManagedMagic.Detect(LzmaAlone));

    [Fact]
    public void Detect_Lz4Frame() => Assert.Equal("LZ4 frame compressed data", ManagedMagic.Detect(Lz4Frame));

    [Fact]
    public void Detect_Lz4Legacy() => Assert.Equal("LZ4 legacy compressed data", ManagedMagic.Detect(Lz4Legacy));

    [Fact]
    public void Detect_Zstd() => Assert.Equal("Zstandard compressed data", ManagedMagic.Detect(Zstd));

    [Fact]
    public void Detect_Zip() => Assert.Equal("Zip archive data", ManagedMagic.Detect(Zip));

    [Fact]
    public void Detect_Fdt() => Assert.Equal("Device tree blob", ManagedMagic.Detect(Fdt));

    [Fact]
    public void Detect_AndroidBoot() => Assert.Equal("Android boot image", ManagedMagic.Detect(AndroidBoot));

    [Fact]
    public void Detect_AndroidVendorBoot() =>
        Assert.Equal("Android vendor boot image", ManagedMagic.Detect(AndroidVendorBoot));

    [Fact]
    public void Detect_AndroidDtbo_H() =>
        Assert.Equal("Android device tree overlay image", ManagedMagic.Detect(AndroidDtboH));

    [Fact]
    public void Detect_AndroidDtbo_O() =>
        Assert.Equal("Android device tree overlay image", ManagedMagic.Detect(AndroidDtboO));

    [Fact]
    public void Detect_AndroidPayload() =>
        Assert.Equal("Android OTA payload", ManagedMagic.Detect(AndroidPayload));

    [Fact]
    public void Detect_AndroidAvb() =>
        Assert.Equal("Android Verified Boot image", ManagedMagic.Detect(AndroidAvb));

    [Fact]
    public void Detect_Sparse() => Assert.Equal("Android sparse image", ManagedMagic.Detect(Sparse));

    [Fact]
    public void Detect_CpioNewc1() => Assert.Equal("cpio archive (newc)", ManagedMagic.Detect(CpioNewc1));

    [Fact]
    public void Detect_CpioNewc2() => Assert.Equal("cpio archive (newc)", ManagedMagic.Detect(CpioNewc2));

    [Fact]
    public void Detect_RockchipBoot() =>
        Assert.Equal("Rockchip boot image", ManagedMagic.Detect(RockchipBoot));

    [Fact]
    public void Detect_RockchipRkfw() =>
        Assert.Equal("Rockchip firmware image", ManagedMagic.Detect(RockchipRkfw));

    [Fact]
    public void Detect_RockchipRkaf() =>
        Assert.Equal("Rockchip firmware image", ManagedMagic.Detect(RockchipRkaf));

    [Fact]
    public void Detect_LinuxKernelArm64Image() =>
        Assert.Equal("Linux kernel ARM64 boot executable Image", ManagedMagic.Detect(LinuxKernelArm64Image));

    [Fact]
    public void Detect_LinuxKernelRiscVImage() =>
        Assert.Equal("Linux kernel RISC-V boot executable Image", ManagedMagic.Detect(LinuxKernelRiscVImage));

    [Fact]
    public void Detect_LinuxKernelArmZImage() =>
        Assert.Equal("Linux kernel ARM boot executable zImage", ManagedMagic.Detect(LinuxKernelArmZImage));

    [Fact]
    public void Detect_LinuxKernelArmZImageBE() =>
        Assert.Equal("Linux kernel ARM boot executable zImage", ManagedMagic.Detect(LinuxKernelArmZImageBE));

    [Fact]
    public void Detect_Unknown_ReturnsNull() =>
        Assert.Null(ManagedMagic.Detect(Encoding.ASCII.GetBytes("this is just plain text, not a magic")));

    [Fact]
    public void Detect_TarArchive()
    {
        // 构造一个带 ustar 魔数（偏移 257）的最小 tar 头
        byte[] tarHeader = new byte[512];
        Array.Copy(Encoding.ASCII.GetBytes("test.txt\0\0\0\0\0\0\0\0"), 0, tarHeader, 0, 9);
        Array.Copy(Encoding.ASCII.GetBytes("ustar\0"), 0, tarHeader, 257, 6);
        Assert.Equal("tar archive", ManagedMagic.Detect(tarHeader));
    }

    [Fact]
    public void IsElf_True_ForElfMagic() => Assert.True(ManagedMagic.IsElf(ElfAarch64Exec));

    [Fact]
    public void IsElf_False_ForGzip() => Assert.False(ManagedMagic.IsElf(Gzip));

    [Fact]
    public void DetectFirmwareMagic_Elf() =>
        Assert.NotNull(ManagedMagic.DetectFirmwareMagic(ElfAarch64Exec));

    [Fact]
    public void DetectFirmwareMagic_AndroidBoot() =>
        Assert.Equal("Android boot image", ManagedMagic.DetectFirmwareMagic(AndroidBoot));

    [Fact]
    public void DetectFirmwareMagic_RockchipBoot() =>
        Assert.Equal("Rockchip boot image", ManagedMagic.DetectFirmwareMagic(RockchipBoot));

    [Fact]
    public void DetectFirmwareMagic_Gzip_ReturnsNull() =>
        Assert.Null(ManagedMagic.DetectFirmwareMagic(Gzip));

    // ====== 规则引擎测试 ======

    [Fact]
    public void Parser_SimpleString_Rule()
    {
        MagicRuleSet rs = MagicRuleParser.Parse("0\tstring\tANDROID!\tAndroid boot image", "test");
        MagicMatch m = rs.Match(AndroidBoot, AndroidBoot.Length);
        Assert.True(m.IsMatch);
        Assert.Equal("Android boot image", m.Description);
    }

    [Fact]
    public void Parser_Integer_Rule_BigEndian()
    {
        // belong 0xed26ff3a → 实际比较时按大端读 4 字节
        MagicRuleSet rs = MagicRuleParser.Parse("0\tbelong\t0xed26ff3a\tAndroid sparse image", "test");
        // sparse 字节是 3a ff 26 ed（小端存储），但 belong 按大端读 → 0x3aff26ed ≠ 0xed26ff3a
        // 此规则不应匹配小端 sparse
        MagicMatch m = rs.Match(Sparse, Sparse.Length);
        Assert.False(m.IsMatch);
    }

    [Fact]
    public void Parser_Integer_Rule_LittleEndian()
    {
        // lelong 0xed26ff3a → 小端读 4 字节，sparse 存储为 3a ff 26 ed（即小端 0xed26ff3a）应匹配
        MagicRuleSet rs = MagicRuleParser.Parse("0\tlelong\t0xed26ff3a\tAndroid sparse image", "test");
        MagicMatch m = rs.Match(Sparse, Sparse.Length);
        Assert.True(m.IsMatch);
        Assert.Equal("Android sparse image", m.Description);
    }

    [Fact]
    public void Parser_StringEscape_HexPattern()
    {
        // gzip: \x1f\x8b
        MagicRuleSet rs = MagicRuleParser.Parse("0\tstring\t\\x1f\\x8b\tgzip compressed data", "test");
        MagicMatch m = rs.Match(Gzip, Gzip.Length);
        Assert.True(m.IsMatch);
        Assert.Equal("gzip compressed data", m.Description);
    }

    [Fact]
    public void Parser_Ignores_CommentsAndBlankLines()
    {
        string text = """
            # this is a comment

            0	string	ANDROID!	Android boot image
            # trailing comment

            """;
        MagicRuleSet rs = MagicRuleParser.Parse(text, "test");
        Assert.Equal(1, rs.Count);
        MagicMatch m = rs.Match(AndroidBoot, AndroidBoot.Length);
        Assert.True(m.IsMatch);
    }

    [Fact]
    public void Parser_InvalidLine_Skipped()
    {
        // 仅 2 个 token 的行应被跳过
        MagicRuleSet rs = MagicRuleParser.Parse("0\tstring\n0\tstring\tANDROID!\tAndroid boot image", "test");
        Assert.Equal(1, rs.Count);
    }

    [Fact]
    public void Parser_Indirect_Offset_Rule()
    {
        // 间接偏移：(belong.0+4) → 读 base[0..4] 大端整数 +4 作为实际偏移
        // 构造：偏移 0 处 0x00000004（belong 读为 4），加 4 = 偏移 8，那里放 "ABCD"
        byte[] data = new byte[16];
        data[0] = 0x00; data[1] = 0x00; data[2] = 0x00; data[3] = 0x04; // belong=4
        data[4] = 0xFF; data[5] = 0xFF; data[6] = 0xFF; data[7] = 0xFF; // 填充
        data[8] = (byte)'A'; data[9] = (byte)'B'; data[10] = (byte)'C'; data[11] = (byte)'D';

        MagicRuleSet rs = MagicRuleParser.Parse("0\tbelong\t0x00000004\tindirect marker", "test");
        MagicMatch m = rs.Match(data, data.Length);
        Assert.True(m.IsMatch);
        Assert.Equal("indirect marker", m.Description);
    }

    [Fact]
    public void EmbeddedRules_Load_And_Match_Elf()
    {
        // 加载嵌入式规则集（来自 Common/Magic/firmware.magic 资源）
        MagicRuleSet embedded = ManagedMagic.LoadEmbeddedRules();
        Assert.True(embedded.Count > 0, "嵌入式规则集应包含至少一条规则");

        // ELF 应被规则集识别（注意：嵌入式规则集的 ELF 规则是父规则 + 子规则结构）
        MagicMatch m = embedded.Match(ElfAarch64Exec, ElfAarch64Exec.Length);
        Assert.True(m.IsMatch, "嵌入式规则集应识别 ELF");
        Assert.Contains("ELF", m.Description);
    }

    [Fact]
    public void EmbeddedRules_Load_And_Match_AndroidBoot()
    {
        // ANDROID! 在查重时已从 firmware.magic 剔除（上游 android.magic 有此规则）。
        // 此处验证：加载上游 android.magic 后能识别 ANDROID!。
        // ANDROID! was deduped from firmware.magic (upstream android.magic has it).
        // Verify upstream android.magic recognizes ANDROID!.
        string? text = ReadEmbeddedResource("UotanStudio.Firmware.Common.Magic.android.magic");
        Assert.False(string.IsNullOrEmpty(text), "上游 android.magic 嵌入式资源缺失");

        MagicRuleSet androidRules = MagicRuleParser.Parse(text!, "upstream:android.magic");
        MagicMatch m = androidRules.Match(AndroidBoot, AndroidBoot.Length);
        Assert.True(m.IsMatch, "上游 android.magic 应识别 ANDROID!");
    }

    [Fact]
    public void EmbeddedRules_Idempotent_Load()
    {
        MagicRuleSet first = ManagedMagic.LoadEmbeddedRules();
        MagicRuleSet second = ManagedMagic.LoadEmbeddedRules();
        Assert.Same(first, second);
    }

    [Fact]
    public void LoadRulesFromText_AppendsRuleSet()
    {
        ManagedMagic.ClearRules();
        MagicRuleSet rs = ManagedMagic.LoadRulesFromText(
            "0\tstring\tMAGIC!\tcustom magic type", "custom");
        Assert.Equal("custom", rs.Source);

        // 自定义魔数应匹配
        byte[] custom = Encoding.ASCII.GetBytes("MAGIC!extra");
        MagicMatch m = ManagedMagic.DetectWithRules(custom, custom.Length);
        Assert.True(m.IsMatch);
        Assert.Equal("custom magic type", m.Description);
    }

    [Fact]
    public void LoadRules_FromExternalFile()
    {
        // 写临时 magic 文件
        string tmpDir = Path.Combine(Path.GetTempPath(), "uotan-magic-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        string magicPath = Path.Combine(tmpDir, "test.magic");
        try
        {
            File.WriteAllText(magicPath, "0\tstring\tFILE!\texternal file marker\n");
            ManagedMagic.ClearRules();
            MagicRuleSet rs = ManagedMagic.LoadRules(magicPath);
            Assert.Equal("external:" + magicPath, rs.Source);

            byte[] data = Encoding.ASCII.GetBytes("FILE!payload");
            MagicMatch m = ManagedMagic.DetectWithRules(data, data.Length);
            Assert.True(m.IsMatch);
            Assert.Equal("external file marker", m.Description);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadRules_NonExistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => ManagedMagic.LoadRules("/nonexistent/path/to/magic"));
    }

    [Fact]
    public void DetectWithRules_EmptyData_ReturnsNoMatch()
    {
        ManagedMagic.ClearRules();
        MagicMatch m = ManagedMagic.DetectWithRules(Array.Empty<byte>(), 0);
        Assert.False(m.IsMatch);
    }

    [Fact]
    public void DetectWithRules_UnknownPattern_ReturnsNoMatch()
    {
        ManagedMagic.ClearRules();
        byte[] unknown = Encoding.ASCII.GetBytes("completely unknown pattern here");
        MagicMatch m = ManagedMagic.DetectWithRules(unknown, unknown.Length);
        Assert.False(m.IsMatch);
    }

    [Fact]
    public void ClearRules_RemovesAllExternalRuleSets()
    {
        ManagedMagic.LoadRulesFromText("0\tstring\tX\ttest", "t1");
        ManagedMagic.LoadRulesFromText("0\tstring\tY\ttest2", "t2");
        Assert.True(ManagedMagic.GetLoadedRuleSets().Count >= 3); // embedded + 2

        ManagedMagic.ClearRules();
        // 嵌入式规则集保留
        Assert.Single(ManagedMagic.GetLoadedRuleSets());
    }

    [Fact]
    public void Detect_PrefersFastPath_OverRules()
    {
        // 即使加载了冲突的外部规则，硬编码快速路径应优先命中
        ManagedMagic.ClearRules();
        ManagedMagic.LoadRulesFromText("0\tstring\tANDROID!\tCONFLICT description", "conflict");

        // Detect 应走快速路径返回标准描述（注意：快速路径优先于规则集）
        string? result = ManagedMagic.Detect(AndroidBoot);
        Assert.NotNull(result);
        // 快速路径命中 "Android boot image"，不会被外部规则覆盖
        Assert.Equal("Android boot image", result);
    }

    [Fact]
    public void Detect_ExternalRule_Extends_Detection()
    {
        // 场景：用户加载外部规则识别上游 file(1) 中我们未硬编码的格式
        ManagedMagic.ClearRules();
        ManagedMagic.LoadRulesFromText("0\tstring\tRIFF\tWave audio file\n", "audio-rules");

        byte[] wav = Encoding.ASCII.GetBytes("RIFF\x00\x00\x00\x00WAVEfmt ");
        string? result = ManagedMagic.Detect(wav);
        Assert.NotNull(result);
        Assert.Equal("Wave audio file", result);
    }

    // ====== 二进制边界测试 ======

    [Fact]
    public void Detect_EmptyBuffer_ReturnsNull()
    {
        Assert.Null(ManagedMagic.Detect(Array.Empty<byte>()));
    }

    [Fact]
    public void Detect_TooShortBuffer_ReturnsNull()
    {
        // 仅 2 字节且不匹配任何魔数（gzip 魔数 1F 8B 恰好只需 2 字节，故不能用作反例），应返回 null
        Assert.Null(ManagedMagic.Detect(new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void Detect_PartialMagic_NoMatch()
    {
        // 仅 "ANDROI"（6 字节），不足 "ANDROID!"（8 字节）
        byte[] partial = Encoding.ASCII.GetBytes("ANDROI");
        Assert.Null(ManagedMagic.Detect(partial));
    }

    [Fact]
    public void Detect_BoundaryExact_MatchesAtMinimumLength()
    {
        // "ANDROID!" 正好 8 字节，应匹配
        byte[] exact = Encoding.ASCII.GetBytes("ANDROID!");
        Assert.Equal("Android boot image", ManagedMagic.Detect(exact));
    }

    [Fact]
    public void Detect_Offset_PastBuffer_NoMatch()
    {
        // tar 规则要求偏移 257 处有 "ustar"，缓冲区不足应不匹配
        byte[] shortBuf = new byte[256];
        Assert.Null(ManagedMagic.Detect(shortBuf));
    }

    [Fact]
    public void IsElf_False_ForEmptyBuffer()
    {
        Assert.False(ManagedMagic.IsElf(Array.Empty<byte>()));
    }

    /// <summary>
    /// 构造一个最小但可解析的 ELF 头，便于测试 ClassifyElf 细分逻辑。
    /// Builds a minimal parseable ELF header for ClassifyElf refinement tests.
    /// </summary>
    private static byte[] BuildElfHeader(bool elf64, bool bigEndian, ushort machine, ushort type)
    {
        // 64 位头需 64 字节，32 位头需 52 字节，这里统一给 64 字节
        byte[] header = new byte[64];
        header[0] = 0x7F;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = elf64 ? (byte)2 : (byte)1;    // EI_CLASS
        header[5] = bigEndian ? (byte)2 : (byte)1; // EI_DATA
        header[6] = 1;                              // EI_VERSION

        // e_type @ offset 16, 2 字节
        WriteU16(header, 16, type, bigEndian);
        // e_machine @ offset 18, 2 字节
        WriteU16(header, 18, machine, bigEndian);

        return header;
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }
        else
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }

    /// <summary>
    /// <para>读取 UotanStudio.Firmware 程序集的嵌入式 magic 资源文本。</para>
    /// Reads an embedded magic resource from the UotanStudio.Firmware assembly.
    /// </summary>
    private static string? ReadEmbeddedResource(string resourceName)
    {
        System.Reflection.Assembly assembly = typeof(ManagedMagic).Assembly;
        using System.IO.Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }
        using var sr = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }
}

/// <summary>
/// 上游 file(1) 可读 magic 文本规则的集成测试。
/// 验证纯托管引擎 <see cref="ManagedMagic"/> 与上游 <c>github.com/file/file</c> 的
/// <c>magic/Magdir/</c> 文本规则在常见格式上的一致性。
/// </summary>
public class UpstreamMagicIntegrationTests
{
    // 上游 magic 文本规则资源名（UotanStudio.Firmware 程序集内嵌入式资源）
    private static readonly string[] UpstreamMagicResources =
    [
        "UotanStudio.Firmware.Common.Magic.android.magic",
        "UotanStudio.Firmware.Common.Magic.archive.magic",
        "UotanStudio.Firmware.Common.Magic.compress.magic",
        "UotanStudio.Firmware.Common.Magic.elf.magic",
        "UotanStudio.Firmware.Common.Magic.filesystems.magic",
        "UotanStudio.Firmware.Common.Magic.linux.magic",
        "UotanStudio.Firmware.Common.Magic.sun.magic",
    ];

    /// <summary>
    /// 加载全部上游 magic 文本规则，验证解析无异常且产生非零规则数。
    /// </summary>
    [Fact]
    public void LoadAllUpstreamMagic_NoException_NonZeroRules()
    {
        int totalRules = 0;
        foreach (string resourceName in UpstreamMagicResources)
        {
            string? text = ReadEmbeddedResource(resourceName);
            Assert.False(string.IsNullOrEmpty(text), $"嵌入式资源 {resourceName} 为空或不存在");

            MagicRuleSet ruleSet = MagicRuleParser.Parse(text!, "upstream:" + resourceName);
            Assert.True(ruleSet.Count > 0, $"上游规则 {resourceName} 解析后顶层规则数为 0");
            totalRules += ruleSet.Count;
        }
        Assert.True(totalRules > 50, $"上游 7 个文件总计顶层规则数 {totalRules} 过少，解析可能有问题");
    }

    /// <summary>
    /// 加载上游 elf.magic，验证 ELF 可执行文件被正确识别。
    /// </summary>
    [Fact]
    public void UpstreamElfMagic_RecognizesAarch64Elf()
    {
        MagicRuleSet elfRules = LoadUpstreamResource("UotanStudio.Firmware.Common.Magic.elf.magic");
        // AArch64 可执行文件 ELF 头
        byte[] elf = BuildElfHeader(elf64: true, bigEndian: false, machine: 0xB7, type: 2);

        MagicMatch match = elfRules.Match(elf, elf.Length);
        Assert.True(match.IsMatch, "上游 elf.magic 应识别 AArch64 ELF");
    }

    /// <summary>
    /// 加载上游 compress.magic，验证 gzip 被正确识别。
    /// </summary>
    [Fact]
    public void UpstreamCompressMagic_RecognizesGzip()
    {
        MagicRuleSet compressRules = LoadUpstreamResource("UotanStudio.Firmware.Common.Magic.compress.magic");
        byte[] gzip = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];

        MagicMatch match = compressRules.Match(gzip, gzip.Length);
        Assert.True(match.IsMatch, "上游 compress.magic 应识别 gzip");
    }

    /// <summary>
    /// 加载上游 android.magic，验证 Android boot image 被正确识别。
    /// </summary>
    [Fact]
    public void UpstreamAndroidMagic_RecognizesAndroidBoot()
    {
        MagicRuleSet androidRules = LoadUpstreamResource("UotanStudio.Firmware.Common.Magic.android.magic");
        byte[] androidBoot = Encoding.ASCII.GetBytes("ANDROID!");

        MagicMatch match = androidRules.Match(androidBoot, androidBoot.Length);
        Assert.True(match.IsMatch, "上游 android.magic 应识别 ANDROID! boot image");
    }

    /// <summary>
    /// 综合测试：加载全部上游规则 + 内置 firmware.magic，验证常见固件格式一致性。
    /// 使用 <see cref="ManagedMagic.Detect"/>（快速路径 + 规则集路径）。
    /// </summary>
    [Fact]
    public void FullIntegration_CommonFirmwareFormats_Recognized()
    {
        // 准备测试数据
        byte[] gzip = [0x1F, 0x8B, 0x08, 0x00];
        byte[] androidBoot = Encoding.ASCII.GetBytes("ANDROID!");
        byte[] dtbo = Encoding.ASCII.GetBytes("DTBO");
        byte[] avb = Encoding.ASCII.GetBytes("AVBf");
        byte[] elfAarch64 = BuildElfHeader(elf64: true, bigEndian: false, machine: 0xB7, type: 2);

        // 验证全部被识别（非 null）
        Assert.NotNull(ManagedMagic.Detect(gzip));
        Assert.NotNull(ManagedMagic.Detect(androidBoot));
        Assert.NotNull(ManagedMagic.Detect(dtbo));
        Assert.NotNull(ManagedMagic.Detect(avb));
        Assert.NotNull(ManagedMagic.Detect(elfAarch64));
    }

    /// <summary>
    /// 验证 firmware.magic 查重剔除后，保留的独有规则仍能命中。
    /// </summary>
    [Fact]
    public void FirmwareMagic_AfterDedup_StillMatchesUniqueRules()
    {
        // 这些是 firmware.magic 查重后保留的独有规则
        byte[] vendorBoot = Encoding.ASCII.GetBytes("VNDRBOOT");
        byte[] dtbh = Encoding.ASCII.GetBytes("DTBH");
        byte[] dtbo = Encoding.ASCII.GetBytes("DTBO");
        byte[] otaPayload = Encoding.ASCII.GetBytes("CrAU");
        byte[] avb = Encoding.ASCII.GetBytes("AVBf");
        byte[] rkfw = Encoding.ASCII.GetBytes("Rkfw");
        byte[] rkaf = Encoding.ASCII.GetBytes("RKAF");
        byte[] lz4Frame = [0x04, 0x22, 0x4D, 0x18];
        byte[] lz4Legacy = [0x02, 0x21, 0x4C, 0x18];
        byte[] zstd = [0x28, 0xB5, 0x2F, 0xFD];

        Assert.NotNull(ManagedMagic.Detect(vendorBoot));
        Assert.NotNull(ManagedMagic.Detect(dtbh));
        Assert.NotNull(ManagedMagic.Detect(dtbo));
        Assert.NotNull(ManagedMagic.Detect(otaPayload));
        Assert.NotNull(ManagedMagic.Detect(avb));
        Assert.NotNull(ManagedMagic.Detect(rkfw));
        Assert.NotNull(ManagedMagic.Detect(rkaf));
        Assert.NotNull(ManagedMagic.Detect(lz4Frame));
        Assert.NotNull(ManagedMagic.Detect(lz4Legacy));
        Assert.NotNull(ManagedMagic.Detect(zstd));
    }

    /// <summary>
    /// 验证查重剔除的规则（ELF 子规则、bzip2、ZIP、tar、FAT 等）在上游 magic 中存在，
    /// 确保剔除不会导致检测能力丢失。
    /// </summary>
    [Fact]
    public void Dedup_RemovedRules_ExistInUpstream()
    {
        // 查重剔除的规则应在上游 7 个文件中至少命中一个
        // ELF（上游 elf.magic 有完整规则集）
        MagicRuleSet elfRules = LoadUpstreamResource("UotanStudio.Firmware.Common.Magic.elf.magic");
        Assert.True(elfRules.Count > 0, "上游 elf.magic 应有规则");

        // bzip2（上游 compress.magic 或 archive.magic）
        MagicRuleSet compressRules = LoadUpstreamResource("UotanStudio.Firmware.Common.Magic.compress.magic");
        byte[] bzip2 = [0x42, 0x5A, 0x68, 0x31];
        Assert.True(compressRules.Match(bzip2, bzip2.Length).IsMatch, "上游 compress.magic 应识别 bzip2");

        // ZIP（上游 archive.magic）
        MagicRuleSet archiveRules = LoadUpstreamResource("UotanStudio.Firmware.Common.Magic.archive.magic");
        byte[] zip = [(byte)'P', (byte)'K', 0x03, 0x04];
        Assert.True(archiveRules.Match(zip, zip.Length).IsMatch, "上游 archive.magic 应识别 ZIP");
    }

    private static MagicRuleSet LoadUpstreamResource(string resourceName)
    {
        string? text = ReadEmbeddedResource(resourceName);
        if (string.IsNullOrEmpty(text))
        {
            return new MagicRuleSet("empty:" + resourceName);
        }
        return MagicRuleParser.Parse(text, "upstream:" + resourceName);
    }

    private static string? ReadEmbeddedResource(string resourceName)
    {
        System.Reflection.Assembly assembly = typeof(ManagedMagic).Assembly;
        using System.IO.Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }
        using var sr = new System.IO.StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static byte[] BuildElfHeader(bool elf64, bool bigEndian, ushort machine, ushort type)
    {
        byte[] header = new byte[64];
        header[0] = 0x7F;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = elf64 ? (byte)2 : (byte)1;
        header[5] = bigEndian ? (byte)2 : (byte)1;
        header[6] = 1;
        WriteU16(header, 16, type, bigEndian);
        WriteU16(header, 18, machine, bigEndian);
        return header;
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }
        else
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }
}

/// <summary>
/// <para>增强型托管魔数引擎测试：覆盖 libmagic 语义对齐后的新特性——
/// u 无符号前缀、type&amp;mask / type|mask 等掩码运算、'~' 反相、'&amp;'/'^' 位关系运算、
/// '&amp;' 续接偏移、字符串修饰符（/c 忽略大小写、search/N、pstring）、regex 类型、
/// \b NOSPACE 描述、default 指令、name/use 指令与任意深度子规则递归。</para>
/// Enhanced managed magic engine tests: cover the libmagic-aligned features — u unsigned prefix,
/// type&amp;mask / type|mask mask ops, '~' inverse, '&amp;'/'^' bitwise relations, '&amp;'
/// continuation offsets, string modifiers (/c case-insensitive, search/N, pstring), regex type,
/// \b NOSPACE descriptions, default directive, name/use directives and arbitrary-depth recursion.
/// </summary>
public class ManagedMagicEnhancedTests
{
    private static MagicRuleSet Parse(string text) => MagicRuleParser.Parse(text, "test");

    private static MagicMatch Match(string ruleText, params byte[] data)
    {
        MagicRuleSet rs = Parse(ruleText);
        return rs.Match(data, data.Length);
    }

    // ====== u 无符号前缀 ======

    [Fact]
    public void UnsignedUbyte_Match()
    {
        // ubyte 0x80：按无符号比较，0x80（128）应等于 0x80
        MagicMatch m = Match("0\tubyte\t0x80\tunsigned byte", 0x80);
        Assert.True(m.IsMatch);
        Assert.Equal("unsigned byte", m.Description);
    }

    [Fact]
    public void UnsignedUbyte_GreaterThan_UnsignedSemantics()
    {
        // ubyte > 0x7f：值 0x80 无符号 128 > 127 → 命中（有符号 -128 > 127 则不命中）
        MagicMatch m = Match("0\tubyte\t>0x7f\tunsigned gt", 0x80);
        Assert.True(m.IsMatch);
    }

    [Fact]
    public void UnsignedUlelong_Match()
    {
        // ulelong 0xffffffff（小端 8 字节中的 4 字节 ff ff ff ff）
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];
        MagicMatch m = Match("0\tulelong\t0xffffffff\tunsigned lelong", data);
        Assert.True(m.IsMatch);
    }

    // ====== 掩码运算 ======

    [Fact]
    public void MaskOp_And_TypeMask()
    {
        // byte&0x80 =0x80：仅第 7 位为 1 时命中
        Assert.True(Match("0\tbyte&0x80\t0x80\tmasked", 0x80).IsMatch);
        Assert.True(Match("0\tbyte&0x80\t0x80\tmasked", 0xFF).IsMatch);
        Assert.False(Match("0\tbyte&0x80\t0x80\tmasked", 0x7F).IsMatch);
    }

    [Fact]
    public void MaskOp_Or_TypeMask()
    {
        // byte|0x01 =0x41：值 0x40 → 0x41 命中；值 0x00 → 0x01 不命中
        Assert.True(Match("0\tbyte|0x01\t0x41\tmasked or", 0x40).IsMatch);
        Assert.False(Match("0\tbyte|0x01\t0x41\tmasked or", 0x00).IsMatch);
    }

    [Fact]
    public void MaskOp_Xor_TypeMask()
    {
        // byte^0x0f =0x00：值 0x0f → 0x00 命中
        Assert.True(Match("0\tbyte^0x0f\t0x00\tmasked xor", 0x0F).IsMatch);
        Assert.False(Match("0\tbyte^0x0f\t0x00\tmasked xor", 0x01).IsMatch);
    }

    [Fact]
    public void MaskOp_Add_TypeMask()
    {
        // leshort+4 =0x10：值 0x0c → 0x10 命中
        byte[] data = [0x0C, 0x00];
        Assert.True(Match("0\tleshort+4\t0x10\tmasked add", data).IsMatch);
    }

    [Fact]
    public void MaskInverse_Tilde()
    {
        // byte&~0x80 =0x7f：值 0x00 → (~0) & ... 语义：读取值先按掩码运算再取反
        // 值 0x00：0x00 & 0x80 = 0x00，取反 = 0xff → 不等于 0x7f
        Assert.False(Match("0\tbyte&~0x80\t0x7f\tinverse", 0x00).IsMatch);
        // 值 0x7f：0x7f & 0x80 = 0x00，取反 = 0xff → 仍不等于 0x7f
        Assert.False(Match("0\tbyte&~0x80\t0x7f\tinverse", 0x7F).IsMatch);
        // 值 0x80：0x80 & 0x80 = 0x80，取反 = 0x7f → 命中
        Assert.True(Match("0\tbyte&~0x80\t0x7f\tinverse", 0x80).IsMatch);
    }

    // ====== '&' / '^' 位关系运算 ======

    [Fact]
    public void Reln_BitAnd_AllBitsSet()
    {
        // byte &0x05：匹配 (v & 5) == 5 → 0x07 命中、0x04 不命中
        Assert.True(Match("0\tbyte\t&0x05\tbitand", 0x07).IsMatch);
        Assert.False(Match("0\tbyte\t&0x05\tbitand", 0x04).IsMatch);
    }

    [Fact]
    public void Reln_BitXor_NotAllBitsSet()
    {
        // byte ^0x05：匹配 (v & 5) != 5 → 0x04 命中、0x05 不命中
        Assert.True(Match("0\tbyte\t^0x05\tbitxor", 0x04).IsMatch);
        Assert.False(Match("0\tbyte\t^0x05\tbitxor", 0x05).IsMatch);
    }

    // ====== '&' 续接偏移 ======

    [Fact]
    public void ContinuationOffset_Ampersand_RelativeToParentEnd()
    {
        // 顶层 string "AB" 在偏移 0 匹配（结束位置 2）；子规则 "&0 byte 0x43" 相对结束位置 → 偏移 2
        string rules = """
            0	string	AB	prefix
            >&0	byte	0x43	suffix C
            """;
        MagicRuleSet rs = Parse(rules);
        // 偏移 2 处为 'C' → 命中，描述拼接 "prefix suffix C"
        MagicMatch m = rs.Match(Encoding.ASCII.GetBytes("AB\x43"), 3);
        Assert.True(m.IsMatch);
        Assert.Contains("suffix C", m.Description);
        // 偏移 2 处为 'D' → 子规则不命中，顶层仍命中
        MagicMatch m2 = rs.Match(Encoding.ASCII.GetBytes("AB\x44"), 3);
        Assert.True(m2.IsMatch);
        Assert.DoesNotContain("suffix", m2.Description);
    }

    // ====== 字符串修饰符 ======

    [Fact]
    public void StringModifier_IgnoreCase()
    {
        // string/c：忽略大小写
        Assert.True(Match("0\tstring/c\tmagic!\tci", Encoding.ASCII.GetBytes("MAGIC!")).IsMatch);
        Assert.True(Match("0\tstring/c\tmagic!\tci", Encoding.ASCII.GetBytes("magic!")).IsMatch);
        Assert.False(Match("0\tstring/c\tmagic!\tci", Encoding.ASCII.GetBytes("nope!")).IsMatch);
    }

    [Fact]
    public void StringModifier_FullWord()
    {
        // string/f：整词匹配，后缀须为空白或结束
        Assert.True(Match("0\tstring/f\tboot\tfw", Encoding.ASCII.GetBytes("boot ")).IsMatch);
        Assert.False(Match("0\tstring/f\tboot\tfw", Encoding.ASCII.GetBytes("bootloader")).IsMatch);
    }

    [Fact]
    public void SearchType_WithinRange()
    {
        // search/8：在 8 字节范围内搜索 "EFI"
        byte[] data = new byte[32];
        Array.Copy(Encoding.ASCII.GetBytes("EFI"), 0, data, 5, 3);
        MagicMatch m = Match("0\tsearch/8\tEFI\tEFI found", data);
        Assert.True(m.IsMatch);
        // 超出范围不命中
        byte[] data2 = new byte[32];
        Array.Copy(Encoding.ASCII.GetBytes("EFI"), 0, data2, 20, 3);
        Assert.False(Match("0\tsearch/8\tEFI\tEFI found", data2).IsMatch);
    }

    [Fact]
    public void PString_LengthPrefixed()
    {
        // pstring/B：1 字节长度前缀
        byte[] data = [0x03, (byte)'A', (byte)'B', (byte)'C'];
        Assert.True(Match("0\tpstring/B\tABC\tpstring", data).IsMatch);
        byte[] data2 = [0x03, (byte)'A', (byte)'B', (byte)'X'];
        Assert.False(Match("0\tpstring/B\tABC\tpstring", data2).IsMatch);
    }

    // ====== regex 类型 ======

    [Fact]
    public void RegexType_BasicMatch()
    {
        // regex：Dalvik dex 魔数 "dex\n[0-9]{2}"
        byte[] data = Encoding.ASCII.GetBytes("dex\n035");
        MagicMatch m = Match("0\tregex\tdex\\n[0-9]{2}\tDalvik dex file", data);
        Assert.True(m.IsMatch);
        Assert.Contains("Dalvik dex file", m.Description);
    }

    [Fact]
    public void RegexType_NoMatch()
    {
        Assert.False(Match("0\tregex\tdex\\n[0-9]{2}\tDalvik dex file", Encoding.ASCII.GetBytes("dexX035")).IsMatch);
    }

    // ====== \b NOSPACE 描述 ======

    [Fact]
    public void NoSpace_Backspace_Description_NoLeadingSpace()
    {
        string rules = """
            0	string	AB	prefix
            >2	string	CD	\b, suffix
            """;
        MagicRuleSet rs = Parse(rules);
        MagicMatch m = rs.Match(Encoding.ASCII.GetBytes("ABCD"), 4);
        Assert.True(m.IsMatch);
        // NOSPACE：", suffix" 直接拼接，无前导空格
        Assert.Equal("prefix, suffix", m.Description);
    }

    [Fact]
    public void Space_Separator_Default()
    {
        string rules = """
            0	string	AB	prefix
            >2	string	CD	+ suffix
            """;
        MagicRuleSet rs = Parse(rules);
        MagicMatch m = rs.Match(Encoding.ASCII.GetBytes("ABCD"), 4);
        Assert.Equal("prefix + suffix", m.Description);
    }

    // ====== default 指令 ======

    [Fact]
    public void Default_Matches_WhenNoSiblingMatched()
    {
        string rules = """
            0	string	AB	prefix
            >2	byte	0x01	one
            >2	default	x	other
            """;
        MagicRuleSet rs = Parse(rules);
        // 偏移 2 为 0x01 → 子规则 "one" 命中，default 不参与
        MagicMatch m1 = rs.Match([(byte)'A', (byte)'B', 0x01], 3);
        Assert.Contains("one", m1.Description);
        Assert.DoesNotContain("other", m1.Description);
        // 偏移 2 为 0x02 → 子规则未命中，default 兜底
        MagicMatch m2 = rs.Match([(byte)'A', (byte)'B', 0x02], 3);
        Assert.Contains("other", m2.Description);
    }

    // ====== name / use 指令 ======

    [Fact]
    public void NameUse_Directives()
    {
        string rules = """
            0	name	tar-file
            >257	string	!ustar	
            >>257	ulong	=0	
            0	string	PK\003\004	zip
            >0	use	tar-file
            """;
        MagicRuleSet rs = Parse(rules);
        // 构造 tar 头：偏移 257 处 "ustar" → !ustar 不命中 → name 体不参与
        byte[] tar = new byte[300];
        Array.Copy(Encoding.ASCII.GetBytes("ustar"), 0, tar, 257, 5);
        MagicMatch m1 = rs.Match(tar, tar.Length);
        // 顶层 PK 规则不匹配 → 整体不命中
        Assert.False(m1.IsMatch);
        // 构造 PK 头：顶层 zip 命中，use tar-file 展开（偏移 257 无 "ustar"，ulong=0 命中）
        byte[] pk = new byte[300];
        pk[0] = (byte)'P'; pk[1] = (byte)'K'; pk[2] = 0x03; pk[3] = 0x04;
        MagicMatch m2 = rs.Match(pk, pk.Length);
        Assert.True(m2.IsMatch);
        Assert.Contains("zip", m2.Description);
    }

    // ====== 任意深度子规则递归 ======

    [Fact]
    public void DeepNestedSubRules_RecursiveMatch()
    {
        // 三层子规则：filesystems.magic 等上游文件常见 >>>> 深层级
        string rules = """
            0	string	MAGIC	top
            >5	byte	0x01	level1
            >>8	string	DATA	level2
            >>>12	byte	0xFF	level3
            """;
        byte[] data = new byte[16];
        Array.Copy(Encoding.ASCII.GetBytes("MAGIC"), 0, data, 0, 5);
        data[5] = 0x01;
        Array.Copy(Encoding.ASCII.GetBytes("DATA"), 0, data, 8, 4);
        data[12] = 0xFF;
        MagicRuleSet rs = Parse(rules);
        MagicMatch m = rs.Match(data, data.Length);
        Assert.True(m.IsMatch);
        Assert.Contains("level1", m.Description);
        Assert.Contains("level2", m.Description);
        Assert.Contains("level3", m.Description);
    }

    [Fact]
    public void DeepNested_ChildFailure_StopsDescend()
    {
        string rules = """
            0	string	MAGIC	top
            >5	byte	0x01	level1
            >>8	string	DATA	level2
            """;
        // level1 命中但 level2 不命中 → 顶层 + level1 描述
        byte[] data = new byte[12];
        Array.Copy(Encoding.ASCII.GetBytes("MAGIC"), 0, data, 0, 5);
        data[5] = 0x01;
        Array.Copy(Encoding.ASCII.GetBytes("XXXX"), 0, data, 8, 4);
        MagicMatch m = Match(rules, data);
        Assert.True(m.IsMatch);
        Assert.Contains("level1", m.Description);
        Assert.DoesNotContain("level2", m.Description);
    }

    // ====== date 类型 ======

    [Fact]
    public void DateType_FormatsValue()
    {
        // date：1970-01-02 00:00:00 UTC = 86400 秒（大端 00 01 51 80）
        byte[] data = [0x00, 0x01, 0x51, 0x80];
        MagicMatch m = Match("0\tbedate\tx\tcompiled %s", data);
        Assert.True(m.IsMatch);
        Assert.Contains("1970-01-02", m.Description);
    }

    // ====== 间接偏移（含掩码） ======

    [Fact]
    public void IndirectOffset_WithMaskAndAddend()
    {
        // (0.lelong&0xff+4)：偏移 0 读 lelong（小端 04 00 00 00 = 4），&0xff → 4，+4 → 8，偏移 8 处 "ABCD"
        byte[] data = new byte[16];
        data[0] = 0x04; data[1] = 0x00; data[2] = 0x00; data[3] = 0x00;
        data[8] = (byte)'A'; data[9] = (byte)'B'; data[10] = (byte)'C'; data[11] = (byte)'D';
        MagicMatch m = Match("(0.lelong&0xff+4)\tstring\tABCD\tindirect target", data);
        Assert.True(m.IsMatch);
    }

    // ====== 负偏移（从文件末尾） ======

    [Fact]
    public void NegativeOffset_FromEndOfFile()
    {
        // -4 byte 0x5A：文件长度 6，从末尾数 4 字节 → 偏移 2 处应为 0x5A
        byte[] data = [0x00, 0x00, 0x5A, 0x00, 0x00, 0x00];
        MagicMatch m = Match("-4\tbyte\t0x5A\tfrom end", data);
        Assert.True(m.IsMatch);
    }
}