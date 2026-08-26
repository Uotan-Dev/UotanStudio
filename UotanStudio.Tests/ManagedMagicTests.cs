using System;
using System.IO;
using System.Text;
using UotanStudio.Firmware.Common;
using Xunit;

namespace UotanStudio.Tests;

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

    [Fact]
    public void Detect_Elf_AArch64_Executable()
    {
        string result = ManagedMagic.Detect(ElfAarch64Exec);
        Assert.Contains("AArch64", result);
        Assert.Contains("executable", result);
    }

    [Fact]
    public void Detect_Elf_ARM_Shared()
    {
        string result = ManagedMagic.Detect(ElfArmShared);
        Assert.Contains("ARM", result);
        Assert.Contains("shared object", result);
    }

    [Fact]
    public void Detect_Elf_X86_64_Executable()
    {
        string result = ManagedMagic.Detect(ElfX86_64Exec);
        Assert.Contains("x86-64", result);
        Assert.Contains("executable", result);
    }

    [Fact]
    public void Detect_Elf_386_Relocatable()
    {
        string result = ManagedMagic.Detect(Elf386Relocatable);
        Assert.Contains("x86", result);
        Assert.Contains("relocatable", result);
    }

    [Fact]
    public void Detect_Elf_RiscV_Shared()
    {
        string result = ManagedMagic.Detect(ElfRiscVShared);
        Assert.Contains("RISC-V", result);
        Assert.Contains("shared object", result);
    }

    [Fact]
    public void Detect_Elf_Minimal_WithoutMachine()
    {
        // 仅 8 字节头，e_machine 字段缺失 → 应回退为 "ELF executable"
        string result = ManagedMagic.Detect(ElfMinimal);
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
        Assert.Equal(1, ManagedMagic.GetLoadedRuleSets().Count);
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
