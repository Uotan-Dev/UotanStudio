using System.Reflection;
using System.Text;

namespace UotanStudio.Firmware.Common;

public static class ManagedMagic
{
    private const string EmbeddedMagicResourceName = "UotanStudio.Firmware.Common.Magic.firmware.magic";

    private static readonly List<MagicRuleSet> _loadedRuleSets = new();
    private static MagicRuleSet? _embeddedRuleSet;
    private static bool _embeddedLoaded;

    /// <summary>
    /// Sniffs the file header magic and returns a human-readable type description; returns null if unknown.
    /// <para>嗅探文件头魔数并返回人类可读的类型描述；未知类型返回 null。</para>
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {

        string? fast = DetectFast(header);
        if (fast is not null)
        {
            return fast;
        }


        MagicMatch match = DetectWithRules(header.ToArray(), header.Length);
        return match.IsMatch ? match.Description : null;
    }

    /// <summary>
    /// Hardcoded fast-path covering ELF refinement, compression, Android variants, Rockchip, FDT, cpio, filesystems.
    /// <para>硬编码快速路径：覆盖 ELF 细分、压缩、Android 系列、Rockchip、FDT、cpio、文件系统等。</para>
    /// </summary>
    private static string? DetectFast(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        {
            return ClassifyElf(header);
        }

        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return "gzip compressed data";
        }

        if (header.Length >= 3 && header[0] == 0x42 && header[1] == 0x5A && header[2] == 0x68)
        {
            return "bzip2 compressed data";
        }


        if (header.Length >= 6 && header[0] == 0xFD && header[1] == 0x37 && header[2] == 0x7A
            && header[3] == 0x58 && header[4] == 0x5A && header[5] == 0x00)
        {
            return "XZ compressed data";
        }


        if (header.Length >= 3 && header[0] == 0x5D && header[1] == 0x00 && header[2] == 0x00)
        {
            return "LZMA compressed data";
        }


        if (header.Length >= 4 && header[0] == 0x04 && header[1] == 0x22 && header[2] == 0x4D && header[3] == 0x18)
        {
            return "LZ4 frame compressed data";
        }


        if (header.Length >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
        {
            return "Zstandard compressed data";
        }


        if (header.Length >= 4 && header[0] == 0x02 && header[1] == 0x21 && header[2] == 0x4C && header[3] == 0x18)
        {
            return "LZ4 legacy compressed data";
        }


        if (header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K' && header[2] == 0x03 && header[3] == 0x04)
        {
            return "Zip archive data";
        }


        if (header.Length >= 8 && header[0] == (byte)'A' && header[1] == (byte)'N' && header[2] == (byte)'D'
            && header[3] == (byte)'R' && header[4] == (byte)'O' && header[5] == (byte)'I' && header[6] == (byte)'D' && header[7] == (byte)'!')
        {
            return "Android boot image";
        }


        if (header.Length >= 8 && header[0] == (byte)'V' && header[1] == (byte)'N' && header[2] == (byte)'D'
            && header[3] == (byte)'R' && header[4] == (byte)'B' && header[5] == (byte)'O' && header[6] == (byte)'O' && header[7] == (byte)'T')
        {
            return "Android vendor boot image";
        }


        if (header.Length >= 4 && header[0] == (byte)'D' && header[1] == (byte)'T' && header[2] == (byte)'B' && (header[3] == (byte)'H' || header[3] == (byte)'O'))
        {
            return "Android device tree overlay image";
        }


        if (header.Length >= 4 && header[0] == (byte)'A' && header[1] == (byte)'B' && header[2] == (byte)'O' && header[3] == (byte)'O')
        {
            return "Android boot image (v4)";
        }


        if (header.Length >= 4 && header[0] == (byte)'C' && header[1] == (byte)'r' && header[2] == (byte)'A' && header[3] == (byte)'U')
        {
            return "Android OTA payload";
        }


        if (header.Length >= 4 && header[0] == (byte)'A' && header[1] == (byte)'V' && header[2] == (byte)'B' && header[3] == (byte)'f')
        {
            return "Android Verified Boot image";
        }


        if (header.Length >= 4 && header[0] == 0x3A && header[1] == 0xFF && header[2] == 0x26 && header[3] == 0xED)
        {
            return "Android sparse image";
        }


        if (header.Length >= 6 && header[0] == (byte)'0' && header[1] == (byte)'7' && header[2] == (byte)'0'
            && header[3] == (byte)'7' && (header[4] == (byte)'0' || header[4] == (byte)'1') && (header[5] == (byte)'0' || header[5] == (byte)'1' || header[5] == (byte)'2'))
        {
            return "cpio archive (newc)";
        }


        if (header.Length >= 4 && header[0] == 0xD0 && header[1] == 0x0D && header[2] == 0xFE && header[3] == 0xED)
        {
            return "Device tree blob";
        }


        if (header.Length >= 0x3C && header[0x38] == (byte)'A' && header[0x39] == (byte)'R'
            && header[0x3A] == (byte)'M' && header[0x3B] == 0x64)
        {
            return "Linux kernel ARM64 boot executable Image";
        }


        if (header.Length >= 0x3C && header[0x38] == (byte)'R' && header[0x39] == (byte)'S'
            && header[0x3A] == (byte)'C' && header[0x3B] == 0x05)
        {
            return "Linux kernel RISC-V boot executable Image";
        }


        if (header.Length >= 0x28)
        {
            bool leArm = header[0x24] == 0x18 && header[0x25] == 0x28 && header[0x26] == 0x6F && header[0x27] == 0x01;
            bool beArm = header[0x24] == 0x01 && header[0x25] == 0x6F && header[0x26] == 0x28 && header[0x27] == 0x18;
            if (leArm || beArm)
            {
                return "Linux kernel ARM boot executable zImage";
            }
        }


        if (header.Length >= 0x3A && header[0x38] == 0x53 && header[0x39] == 0xEF)
        {
            return "ext2/3/4 filesystem";
        }


        if (header.Length >= 4 && header[0] == 0x73 && header[1] == 0x71 && header[2] == 0x73 && header[3] == 0x68)
        {
            return "squashfs filesystem";
        }


        if (header.Length >= 4 && header[0] == 0xE0 && header[1] == 0xE5 && header[2] == 0xE1 && header[3] == 0xE2)
        {
            return "erofs filesystem";
        }


        if (header.Length >= 0x39 && header[0x36] == (byte)'F' && header[0x37] == (byte)'A' && header[0x38] == (byte)'T')
        {
            return "FAT filesystem";
        }
        if (header.Length >= 0x56 && header[0x52] == (byte)'F' && header[0x53] == (byte)'A' && header[0x54] == (byte)'T')
        {
            return "FAT32 filesystem";
        }


        if (header.Length >= 8 && header[3] == (byte)'N' && header[4] == (byte)'T' && header[5] == (byte)'F' && header[6] == (byte)'S')
        {
            return "NTFS filesystem";
        }


        if (header.Length >= 4 && header[0] == (byte)'B' && header[1] == (byte)'O' && header[2] == (byte)'O' && header[3] == (byte)'T')
        {
            return "Rockchip boot image";
        }


        if (header.Length >= 4 && header[0] == (byte)'R' && header[1] == (byte)'k' && header[2] == (byte)'f' && header[3] == (byte)'w')
        {
            return "Rockchip firmware image";
        }
        if (header.Length >= 4 && header[0] == (byte)'R' && header[1] == (byte)'K' && header[2] == (byte)'A' && header[3] == (byte)'F')
        {
            return "Rockchip firmware image";
        }


        if (header.Length >= 262 && header[257] == (byte)'u' && header[258] == (byte)'s'
            && header[259] == (byte)'t' && header[260] == (byte)'a' && header[261] == (byte)'r')
        {
            return "tar archive";
        }

        return null;
    }

    /// <summary>
    /// Refines the ELF header by machine class, endianness and word size for a more precise description.
    /// <para>对 ELF 头部做机器类别/字节序/位宽的细分判定，返回更精确的类型描述。</para>
    /// </summary>
    private static string ClassifyElf(ReadOnlySpan<byte> header)
    {
        if (header.Length < 20)
        {
            return "ELF executable";
        }

        bool is64Bit = header[4] == 2;
        bool bigEndian = header[5] == 2;

        ushort machine = bigEndian
            ? (ushort)((header[18] << 8) | header[19])
            : (ushort)((header[19] << 8) | header[18]);

        ushort type = bigEndian
            ? (ushort)((header[16] << 8) | header[17])
            : (ushort)((header[17] << 8) | header[16]);

        string arch = machine switch
        {
            0x03 => "x86",
            0x28 => "ARM",
            0x3E => "x86-64",
            0xB7 => "AArch64",
            0xF3 => "RISC-V",
            _ => "unknown"
        };

        string kind = type switch
        {
            1 => "relocatable",
            2 => "executable",
            3 => "shared object",
            4 => "core dump",
            _ => "executable"
        };

        string bits = is64Bit ? "64-bit" : "32-bit";
        return $"ELF {bits} {arch} {kind}";
    }

    /// <summary>
    /// Whether the header starts with the ELF magic.
    /// <para>头部是否 ELF 魔数。</para>
    /// </summary>
    public static bool IsElf(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F';

    /// <summary>
    /// Returns the firmware/Android-specific type description for a known magic, or null.
    /// <para>若文件头匹配已知的 Android/固件相关魔数，返回对应类型；否则返回 null。</para>
    /// </summary>
    public static string? DetectFirmwareMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        {
            return ClassifyElf(header);
        }

        if (header.Length >= 8 && header[0] == (byte)'A' && header[1] == (byte)'N' && header[2] == (byte)'D'
            && header[3] == (byte)'R' && header[4] == (byte)'O' && header[5] == (byte)'I' && header[6] == (byte)'D' && header[7] == (byte)'!')
        {
            return "Android boot image";
        }

        if (header.Length >= 8 && header[0] == (byte)'V' && header[1] == (byte)'N' && header[2] == (byte)'D'
            && header[3] == (byte)'R' && header[4] == (byte)'B' && header[5] == (byte)'O' && header[6] == (byte)'O' && header[7] == (byte)'T')
        {
            return "Android vendor boot image";
        }

        if (header.Length >= 4 && header[0] == (byte)'D' && header[1] == (byte)'T' && header[2] == (byte)'B' && (header[3] == (byte)'H' || header[3] == (byte)'O'))
        {
            return "Android device tree overlay image";
        }

        if (header.Length >= 4 && header[0] == (byte)'C' && header[1] == (byte)'r' && header[2] == (byte)'A' && header[3] == (byte)'U')
        {
            return "Android OTA payload";
        }

        if (header.Length >= 4 && header[0] == (byte)'A' && header[1] == (byte)'V' && header[2] == (byte)'B' && header[3] == (byte)'f')
        {
            return "Android Verified Boot image";
        }

        if (header.Length >= 4 && header[0] == 0x3A && header[1] == 0xFF && header[2] == 0x26 && header[3] == 0xED)
        {
            return "Android sparse image";
        }

        if (header.Length >= 4 && header[0] == 0xD0 && header[1] == 0x0D && header[2] == 0xFE && header[3] == 0xED)
        {
            return "Device tree blob";
        }

        if (header.Length >= 4 && header[0] == (byte)'B' && header[1] == (byte)'O' && header[2] == (byte)'O' && header[3] == (byte)'T')
        {
            return "Rockchip boot image";
        }
        if (header.Length >= 4 && header[0] == (byte)'R' && header[1] == (byte)'k' && header[2] == (byte)'f' && header[3] == (byte)'w')
        {
            return "Rockchip firmware image";
        }
        if (header.Length >= 4 && header[0] == (byte)'R' && header[1] == (byte)'K' && header[2] == (byte)'A' && header[3] == (byte)'F')
        {
            return "Rockchip firmware image";
        }

        return null;
    }


    /// <summary>
    /// Loads the embedded magic rule resource (Common/Magic/firmware.magic).
    /// <para>加载嵌入式 magic 规则资源（Common/Magic/firmware.magic）。</para>
    /// </summary>
    public static MagicRuleSet LoadEmbeddedRules()
    {
        if (_embeddedLoaded && _embeddedRuleSet is not null)
        {
            return _embeddedRuleSet;
        }

        string? text = ReadEmbeddedResource(EmbeddedMagicResourceName);
        if (string.IsNullOrEmpty(text))
        {
            _embeddedRuleSet = new MagicRuleSet("embedded:firmware.magic");
            _embeddedLoaded = true;
            return _embeddedRuleSet;
        }

        _embeddedRuleSet = MagicRuleParser.Parse(text, "embedded:firmware.magic");
        _embeddedLoaded = true;
        return _embeddedRuleSet;
    }

    /// <summary>
    /// Loads an external magic text file and appends it to the rule-set list.
    /// <para>加载外部 magic 文本文件并追加到规则集列表。</para>
    /// </summary>
    public static MagicRuleSet LoadRules(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Magic rule file not found.", path);
        }

        MagicRuleSet ruleSet = MagicRuleParser.ParseFile(path);
        _loadedRuleSets.Add(ruleSet);
        return ruleSet;
    }

    /// <summary>
    /// Loads a rule set directly from text and appends it.
    /// <para>从文本直接加载规则集并追加。</para>
    /// </summary>
    public static MagicRuleSet LoadRulesFromText(string text, string source = "runtime")
    {
        MagicRuleSet ruleSet = MagicRuleParser.Parse(text, source);
        _loadedRuleSets.Add(ruleSet);
        return ruleSet;
    }

    /// <summary>
    /// Clears all loaded external rule sets (the embedded rule set is retained).
    /// <para>清空所有已加载的外部规则集（嵌入式规则集保留）。</para>
    /// </summary>
    public static void ClearRules()
    {
        _loadedRuleSets.Clear();
    }

    /// <summary>
    /// Matches the file header against loaded rule sets (embedded + external, in load order).
    /// <para>使用已加载的规则集（嵌入式 + 外部追加）对文件头进行匹配。</para>
    /// </summary>
    public static MagicMatch DetectWithRules(byte[] fileData, int fileLength)
    {

        MagicRuleSet embedded = LoadEmbeddedRules();
        MagicMatch match = embedded.Match(fileData, fileLength);
        if (match.IsMatch)
        {
            return match;
        }


        foreach (MagicRuleSet ruleSet in _loadedRuleSets)
        {
            match = ruleSet.Match(fileData, fileLength);
            if (match.IsMatch)
            {
                return match;
            }
        }

        return MagicMatch.None;
    }

    /// <summary>
    /// Gets all loaded rule sets (embedded + external), mainly for diagnostics and testing.
    /// <para>获取所有已加载的规则集（嵌入式 + 外部），主要用于诊断与测试。</para>
    /// </summary>
    public static IReadOnlyList<MagicRuleSet> GetLoadedRuleSets()
    {
        var result = new List<MagicRuleSet>();
        result.Add(LoadEmbeddedRules());
        result.AddRange(_loadedRuleSets);
        return result;
    }

    /// <summary>
    /// Reads an embedded resource as text.
    /// <para>读取程序集嵌入式资源文本。</para>
    /// </summary>
    private static string? ReadEmbeddedResource(string resourceName)
    {
        Assembly assembly = typeof(ManagedMagic).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }
}
