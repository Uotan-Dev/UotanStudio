namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Models;

/// <summary>
/// Metadata container describing one analyzed APK; <see cref="Parsing.ApkParser.Analyze"/> fills it in place and leaves unresolved fields at their empty or zero defaults.
/// <para>单个已分析 APK 的元数据容器；<see cref="Parsing.ApkParser.Analyze"/> 就地填充，未解析出的字段保持空值或零默认。</para>
/// </summary>
public sealed class ApkInfo
{
    /// <summary>
    /// Path the APK was read from, stored exactly as supplied by the caller.
    /// <para>读取 APK 时使用的路径，按调用方传入的原样保存。</para>
    /// </summary>
    /// <value>The input path; empty until an analysis sets it. <para>输入路径；分析赋值前为空。</para></value>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Size of the APK file on disk, in bytes, captured at analysis time.
    /// <para>分析时磁盘上 APK 文件的字节大小。</para>
    /// </summary>
    /// <value>Byte count; 0 when unknown. <para>字节数；未知时为 0。</para></value>
    public long FileSize { get; set; }

    /// <summary>
    /// Total number of ZIP entries in the container, including signature and metadata entries.
    /// <para>容器内 ZIP 条目总数，含签名与元数据条目。</para>
    /// </summary>
    /// <value>Entry count; 0 before analysis. <para>条目数；分析前为 0。</para></value>
    public int EntryCount { get; set; }

    /// <summary>APK signature schemes detected in the container (v1, v2 and/or v3).
    /// <para>容器中检测到的 APK 签名方案（v1、v2 和/或 v3）。</para></summary>
    /// <value>Mutable list of scheme identifiers sorted ordinally: v1 needs both META-INF/MANIFEST.MF and a .SF/.RSA/.DSA/.EC entry, v2/v3 come from signing-block IDs; empty means none of the known schemes was found.
    /// <para>按序数排序的方案标识可变列表：v1 需同时存在 META-INF/MANIFEST.MF 与 .SF/.RSA/.DSA/.EC 条目，v2/v3 来自签名块 ID；为空表示未发现任何已知方案。</para></value>
    public List<string> SigningSchemes { get; } = [];

    /// <summary>Known APK hardening marker, when one is detected.
    /// <para>检测到的已知 APK 加固标记（如有）。</para></summary>
    /// <value>Name of the first provider whose marker appears in an entry path (case-insensitive); <see langword="null"/> when no known marker is present.
    /// <para>条目路径中首先命中的加固厂商名称（不区分大小写）；未命中任何已知标记时为 <see langword="null"/>。</para></value>
    public string? HardeningProvider { get; set; }

    /// <summary>
    /// Value of the manifest <c>package</c> attribute.
    /// <para>清单 <c>package</c> 属性的值。</para>
    /// </summary>
    /// <value>The application id; empty when the manifest is absent or the attribute is missing. <para>应用包标识；清单缺失或无该属性时为空。</para></value>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable application label.
    /// <para>可读的应用名称。</para>
    /// </summary>
    /// <value>Preferentially the ARSC-resolved label (localized to the requested language), otherwise the raw manifest <c>label</c> attribute; empty when neither resolves. <para>优先取 ARSC 解析出的标签（按请求语言本地化），否则为清单 <c>label</c> 属性原文；两者皆不可用时为空。</para></value>
    public string ApplicationName { get; set; } = string.Empty;

    internal uint ApplicationLabelReference { get; set; }

    /// <summary>
    /// Value of the manifest <c>versionName</c> attribute.
    /// <para>清单 <c>versionName</c> 属性的值。</para>
    /// </summary>
    /// <value>The user-facing version string; empty when absent. <para>面向用户的版本字符串；缺失时为空。</para></value>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>
    /// Manifest <c>versionCode</c> as an integer (decimal, or hexadecimal with a <c>0x</c> prefix).
    /// <para>整型清单 <c>versionCode</c>（十进制，或带 <c>0x</c> 前缀的十六进制）。</para>
    /// </summary>
    /// <value>The parsed code; 0 when absent or unparseable. <para>解析结果；缺失或无法解析时为 0。</para></value>
    public long VersionCode { get; set; }

    /// <summary>
    /// Minimum SDK level, read from the manifest root or the nested <c>uses-sdk</c> element.
    /// <para>最低 SDK 级别，取自清单根元素或嵌套的 <c>uses-sdk</c> 元素。</para>
    /// </summary>
    /// <value>The parsed level; 0 when absent or unparseable. <para>解析结果；缺失或无法解析时为 0。</para></value>
    public long MinSdkVersion { get; set; }

    /// <summary>
    /// Target SDK level, read from the manifest root or the nested <c>uses-sdk</c> element.
    /// <para>目标 SDK 级别，取自清单根元素或嵌套的 <c>uses-sdk</c> 元素。</para>
    /// </summary>
    /// <value>The parsed level; 0 when absent or unparseable. <para>解析结果；缺失或无法解析时为 0。</para></value>
    public long TargetSdkVersion { get; set; }

    /// <summary>
    /// SDK level the package was compiled against, read from the manifest root or the nested <c>uses-sdk</c> element.
    /// <para>编译所用的 SDK 级别，取自清单根元素或嵌套的 <c>uses-sdk</c> 元素。</para>
    /// </summary>
    /// <value>The parsed level; 0 when absent or unparseable. <para>解析结果；缺失或无法解析时为 0。</para></value>
    public long CompileSdkVersion { get; set; }

    /// <summary>
    /// Declared <c>uses-permission</c> names, de-duplicated.
    /// <para>声明的 <c>uses-permission</c> 名称，已去重。</para>
    /// </summary>
    /// <value>Mutable list sorted ordinally; empty when the manifest is absent or declares none. <para>按序数排序的可变列表；清单缺失或无声明时为空。</para></value>
    public List<string> Permissions { get; } = [];

    /// <summary>
    /// ABI directory names taken from the second path segment of <c>lib/</c> entries, de-duplicated.
    /// <para>取自 <c>lib/</c> 条目路径第二段的 ABI 目录名，已去重。</para>
    /// </summary>
    /// <value>Mutable list sorted ordinally (for example <c>arm64-v8a</c>); empty for a pure-Java package. <para>按序数排序的可变列表（例如 <c>arm64-v8a</c>）；纯 Java 包时为空。</para></value>
    public List<string> Abis { get; } = [];

    /// <summary>
    /// Archive-relative path of the resolved application icon.
    /// <para>解析出的应用图标在包内的相对路径。</para>
    /// </summary>
    /// <value>A bitmap or vector path present in the ZIP; <see langword="null"/> when no icon resolves. <para>ZIP 中确实存在的位图或矢量路径；无法解析时为 <see langword="null"/>。</para></value>
    public string? IconPath { get; set; }

    /// <summary>
    /// Raw bytes of the icon entry named by <see cref="IconPath"/>.
    /// <para><see cref="IconPath"/> 所指图标条目的原始字节。</para>
    /// </summary>
    /// <value>Up to 16 MiB read at analysis time; <see langword="null"/> when no icon path resolved or its entry is missing. <para>分析时读取，上限 16 MiB；图标路径未解析或条目缺失时为 <see langword="null"/>。</para></value>
    public byte[]? IconBytes { get; set; }

    /// <summary>
    /// Resource identifier of the manifest <c>icon</c> attribute used to drive ARSC icon lookup.
    /// <para>用于驱动 ARSC 图标查找的清单 <c>icon</c> 属性资源 ID。</para>
    /// </summary>
    /// <value>Non-zero only while <see cref="Parsing.ApkParser.Analyze"/> resolves the icon; it is reset to 0 before the method returns. <para>仅在 <see cref="Parsing.ApkParser.Analyze"/> 解析图标期间非 0；方法返回前重置为 0。</para></value>
    public uint IconReference { get; set; }

    /// <summary>
    /// Produces a readable description summary (for the properties panel / logs).
    /// <para>生成可读描述摘要（属性面板/日志用）。</para>
    /// </summary>
    /// <returns>One line with the container label, then the package, version and the ABI, permission and entry counts; missing package or version segments are omitted.
    /// <para>一行文本：容器标识后依次是包名、版本以及 ABI、权限、条目数量；缺失的包名或版本段会被省略。</para></returns>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Android Package (APK)");
        if (PackageName.Length > 0)
        {
            sb.Append(" | package: ").Append(PackageName);
        }
        if (VersionName.Length > 0)
        {
            sb.Append(" | version: ").Append(VersionName);
        }
        sb.Append(" | abis: ").Append(Abis.Count);
        sb.Append(" | permissions: ").Append(Permissions.Count);
        sb.Append(" | entries: ").Append(EntryCount);
        return sb.ToString();
    }
}