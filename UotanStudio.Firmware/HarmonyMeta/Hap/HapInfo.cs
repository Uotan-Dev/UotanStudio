namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Kind of HarmonyOS application container described by <see cref="HapInfo"/>.
/// <para><see cref="HapInfo"/> 所描述的 HarmonyOS 应用容器类型。</para>
/// </summary>
public enum HapContainerKind
{
    /// <summary>A single HAP/HSP module.<para>单个 HAP/HSP 模块。</para></summary>
    Module,

    /// <summary>An APP bundle containing one or more HAP/HSP modules.<para>包含一个或多个 HAP/HSP 模块的 APP 包。</para></summary>
    Bundle,
}

/// <summary>
/// Metadata container describing one analyzed HarmonyOS application container; <see cref="HapParser.Analyze"/> fills it in place and leaves unresolved fields at their empty or zero defaults.
/// <para>单个已分析 HarmonyOS 应用容器的元数据容器；<see cref="HapParser.Analyze"/> 就地填充，未解析出的字段保持空值或零默认。</para>
/// </summary>
public sealed class HapInfo
{
    /// <summary>Path the container was read from, stored exactly as supplied.<para>读取容器时使用的路径，按传入原样保存。</para></summary>
    /// <value>The input path; empty until an analysis sets it. <para>输入路径；分析赋值前为空。</para></value>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Size of the container file on disk, in bytes, captured at analysis time.<para>分析时磁盘上容器文件的字节大小。</para></summary>
    /// <value>Byte count; 0 when unknown. <para>字节数；未知时为 0。</para></value>
    public long FileSize { get; set; }

    /// <summary>Total number of ZIP entries in the analyzed container.<para>所分析容器内 ZIP 条目总数。</para></summary>
    /// <value>Entry count; 0 before analysis. <para>条目数；分析前为 0。</para></value>
    public int EntryCount { get; set; }

    /// <summary>Container layout the metadata was read from.<para>读取元数据时所依据的容器布局。</para></summary>
    /// <value>The detected kind. <para>检测出的容器类型。</para></value>
    public HapContainerKind ContainerKind { get; set; }

    /// <summary>Application bundle name declared by <c>module.json</c> or <c>pack.info</c>.<para><c>module.json</c> 或 <c>pack.info</c> 声明的应用包名。</para></summary>
    /// <value>The bundle identifier; empty when undeclared. <para>应用包标识；未声明时为空。</para></value>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>Human-readable application label resolved from the manifest or module metadata.<para>从清单或模块元数据解析出的可读应用名称。</para></summary>
    /// <value>The application label, or the raw resource reference when it cannot be resolved; empty when neither exists. <para>应用名称；无法解析时为资源引用原文；两者皆无时为空。</para></value>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>User-facing version string.<para>面向用户的版本字符串。</para></summary>
    /// <value>The version name; empty when absent. <para>版本名；缺失时为空。</para></value>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>Numeric version code.<para>数字版本号。</para></summary>
    /// <value>The parsed code; 0 when absent or unparseable. <para>解析结果；缺失或无法解析时为 0。</para></value>
    public long VersionCode { get; set; }

    /// <summary>Bundle type declared by the application metadata, for example <c>app</c>.<para>应用元数据声明的包类型，例如 <c>app</c>。</para></summary>
    /// <value>The bundle type; empty when absent. <para>包类型；缺失时为空。</para></value>
    public string BundleType { get; set; } = string.Empty;

    /// <summary>Application vendor declared by the module metadata.<para>模块元数据声明的应用供应商。</para></summary>
    /// <value>The vendor; empty when absent. <para>供应商；缺失时为空。</para></value>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>API release type declared by the module metadata, for example <c>Release</c>.<para>模块元数据声明的 API 发布类型，例如 <c>Release</c>。</para></summary>
    /// <value>The release type; empty when absent. <para>发布类型；缺失时为空。</para></value>
    public string ApiReleaseType { get; set; } = string.Empty;

    /// <summary>Target API version as declared by the module metadata.<para>模块元数据声明的目标 API 版本。</para></summary>
    /// <value>The target API level; 0 when absent. <para>目标 API 级别；缺失时为 0。</para></value>
    public long TargetApiVersion { get; set; }

    /// <summary>Compatible (minimum) API version as declared by the module metadata.<para>模块元数据声明的兼容（最低）API 版本。</para></summary>
    /// <value>The compatible API level; 0 when absent. <para>兼容 API 级别；缺失时为 0。</para></value>
    public long CompatibleApiVersion { get; set; }

    /// <summary>SDK version the module was compiled against, when the metadata carries a textual identifier.<para>模块编译时使用的 SDK 版本（元数据携带文本标识时）。</para></summary>
    /// <value>The compile SDK identifier; empty when absent. <para>编译 SDK 标识；缺失时为空。</para></value>
    public string CompileSdkVersion { get; set; } = string.Empty;

    /// <summary>Device types the application declares support for, sorted ordinally.<para>应用声明支持的设备类型，已按序数排序。</para></summary>
    /// <value>Mutable list; empty when undeclared. <para>可变列表；未声明时为空。</para></value>
    public List<string> DeviceTypes { get; } = [];

    /// <summary>Declared permission names, de-duplicated and sorted ordinally.<para>声明的权限名称，已去重并按序数排序。</para></summary>
    /// <value>Mutable list; empty when undeclared. <para>可变列表；未声明时为空。</para></value>
    public List<string> Permissions { get; } = [];

    /// <summary>Names of abilities declared by the module metadata.<para>模块元数据声明的 Ability 名称。</para></summary>
    /// <value>Mutable list; empty when undeclared. <para>可变列表；未声明时为空。</para></value>
    public List<string> Abilities { get; } = [];

    /// <summary>Names of extension abilities declared by the module metadata.<para>模块元数据声明的 ExtensionAbility 名称。</para></summary>
    /// <value>Mutable list; empty when undeclared. <para>可变列表；未声明时为空。</para></value>
    public List<string> ExtensionAbilities { get; } = [];

    /// <summary>Whether the APP container carries an application-level signature block.<para>APP 容器是否携带应用级签名块。</para></summary>
    /// <value><see langword="true"/> when a signature block was found. <para>发现签名块时为 <see langword="true"/>。</para></value>
    public bool IsAppSigned { get; set; }

    /// <summary>Whether the analyzed module, or a module inside the bundle, carries a HAP signing block.<para>所分析模块或包内模块是否携带 HAP 签名块。</para></summary>
    /// <value><see langword="true"/> when a HAP signing block was found. <para>发现 HAP 签名块时为 <see langword="true"/>。</para></value>
    public bool IsHapSigned { get; set; }

    /// <summary>Signature profile type decoded from the application signature, for example <c>debug</c> or <c>release</c>.<para>从应用签名中解出的 Profile 类型，例如 <c>debug</c> 或 <c>release</c>。</para></summary>
    /// <value>The profile type; empty when unsigned or undecodable. <para>Profile 类型；未签名或无法解析时为空。</para></value>
    public string SignatureType { get; set; } = string.Empty;

    /// <summary>Developer identifier decoded from the application signature profile.<para>从应用签名 Profile 中解出的开发者标识。</para></summary>
    /// <value>The developer id; empty when unsigned or undecodable. <para>开发者标识；未签名或无法解析时为空。</para></value>
    public string DeveloperId { get; set; } = string.Empty;

    /// <summary>Issuer decoded from the application signature profile.<para>从应用签名 Profile 中解出的签发方。</para></summary>
    /// <value>The issuer; empty when unsigned or undecodable. <para>签发方；未签名或无法解析时为空。</para></value>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Not-before timestamp of the application signature validity window, in Unix seconds.<para>应用签名有效期起始时间（Unix 秒）。</para></summary>
    /// <value>The timestamp; 0 when unsigned or absent. <para>时间戳；未签名或缺失时为 0。</para></value>
    public long ValidityNotBefore { get; set; }

    /// <summary>Not-after timestamp of the application signature validity window, in Unix seconds.<para>应用签名有效期结束时间（Unix 秒）。</para></summary>
    /// <value>The timestamp; 0 when unsigned or absent. <para>时间戳；未签名或缺失时为 0。</para></value>
    public long ValidityNotAfter { get; set; }

    /// <summary>Archive-relative path of the resolved application icon inside the analyzed container.<para>解析出的应用图标在所分析容器内的相对路径。</para></summary>
    /// <value>An existing entry path; <see langword="null"/> when no icon resolves. <para>确实存在的条目路径；无法解析时为 <see langword="null"/>。</para></value>
    public string? IconPath { get; set; }

    /// <summary>Raw bytes of the icon entry named by <see cref="IconPath"/>.<para><see cref="IconPath"/> 所指图标条目的原始字节。</para></summary>
    /// <value>Up to 16 MiB read at analysis time; <see langword="null"/> when no icon resolves. <para>分析时读取，上限 16 MiB；无法解析时为 <see langword="null"/>。</para></value>
    public byte[]? IconBytes { get; set; }

    /// <summary>
    /// Produces a readable one-line description of the analyzed container.
    /// <para>生成所分析容器的可读单行描述。</para>
    /// </summary>
    /// <returns>One line with the container label, package, version and the module, permission and entry counts.
    /// <para>一行文本：容器标识、包名、版本以及模块、权限与条目数量。</para></returns>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ContainerKind == HapContainerKind.Bundle ? "HarmonyOS Application (APP)" : "HarmonyOS Package (HAP)");
        if (PackageName.Length > 0)
        {
            sb.Append(" | bundle: ").Append(PackageName);
        }
        if (VersionName.Length > 0)
        {
            sb.Append(" | version: ").Append(VersionName);
        }
        sb.Append(" | devices: ").Append(DeviceTypes.Count);
        sb.Append(" | permissions: ").Append(Permissions.Count);
        sb.Append(" | entries: ").Append(EntryCount);
        return sb.ToString();
    }
}