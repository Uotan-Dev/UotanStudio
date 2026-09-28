namespace UotanStudio.Firmware.VendorFirmware.Oppo.Models;

/// <summary>
/// Immutable description of an Oppo firmware archive produced by a format parser.
/// <para>由格式解析器生成的、对 Oppo 固件归档的不可变描述。</para>
/// </summary>
public sealed class OppMetadata
{
    /// <summary>Human-readable name of the detected format, used as a display label. <para>所检测格式的可读名称，用作显示标签。</para></summary>
    /// <value>Never <see langword="null"/>; an empty string when the parser recorded no name. <para>绝不为 <see langword="null"/>；解析器未记录名称时为空字符串。</para></value>
    public string FormatName { get; init; } = string.Empty;

    /// <summary>The container generation this archive was parsed as. <para>该归档被解析成的容器类型。</para></summary>
    /// <value>One of the <see cref="OppFormat"/> members. <para><see cref="OppFormat"/> 的某个成员。</para></value>
    public OppFormat Format { get; init; }

    /// <summary>Version reported by the header, or <see langword="null"/> when the format carries no version field. <para>头部报告的版本；格式不含版本字段时为 <see langword="null"/>。</para></summary>
    /// <value>The parsed version, or <see langword="null"/>. <para>已解析的版本，或 <see langword="null"/>。</para></value>
    public Version? FormatVersion { get; init; }

    /// <summary>Total size in bytes of the parsed container; for a ZIP-wrapped OFP this is the outer archive size. <para>所解析容器的总字节数；对 ZIP 包裹的 OFP 而言为外层归档大小。</para></summary>
    /// <value>The byte length as recorded during parsing. <para>解析时记录的字节长度。</para></value>
    public long FileSize { get; init; }

    /// <summary>Number of entries the archive declares. <para>归档声明的条目数量。</para></summary>
    /// <value>The declared entry count. <para>声明的条目数量。</para></value>
    public int EntryCount { get; init; }

    /// <summary>Device project or code name, or <see langword="null"/> when absent. <para>设备项目名或代号；缺失时为 <see langword="null"/>。</para></summary>
    /// <value>The project name, or <see langword="null"/>. <para>项目名，或 <see langword="null"/>。</para></value>
    public string? ProjectName { get; init; }

    /// <summary>CPU or SoC model recorded in the metadata, or <see langword="null"/> when absent. <para>元数据中记录的 CPU/SoC 型号；缺失时为 <see langword="null"/>。</para></summary>
    /// <value>The CPU model, or <see langword="null"/>. <para>CPU 型号，或 <see langword="null"/>。</para></value>
    public string? CpuModel { get; init; }

    /// <summary>Flash storage type recorded in the metadata, or <see langword="null"/> when absent. <para>元数据中记录的闪存类型；缺失时为 <see langword="null"/>。</para></summary>
    /// <value>The flash type, or <see langword="null"/>. <para>闪存类型，或 <see langword="null"/>。</para></value>
    public string? FlashType { get; init; }

    /// <summary>Firmware label or build name, or <see langword="null"/> when absent. <para>固件标签或构建名；缺失时为 <see langword="null"/>。</para></summary>
    /// <value>The firmware name, or <see langword="null"/>. <para>固件名，或 <see langword="null"/>。</para></value>
    public string? FirmwareName { get; init; }

    /// <summary>Project identifier, or <see langword="null"/> when absent. <para>项目标识符；缺失时为 <see langword="null"/>。</para></summary>
    /// <value>The project identifier, or <see langword="null"/>. <para>项目标识符，或 <see langword="null"/>。</para></value>
    public string? ProjectId { get; init; }

    /// <summary>Whether the archive is an OFP wrapped inside a password-protected ZIP container. <para>该归档是否为包裹在带密码 ZIP 容器内的 OFP。</para></summary>
    /// <value><see langword="true"/> when the container is ZIP-wrapped; otherwise <see langword="false"/>. <para>容器为 ZIP 包裹时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></value>
    public bool IsZipWrapped { get; init; }

    /// <summary>Identifier of the decryption key associated with the archive, or <see langword="null"/> when none is recorded. <para>与归档关联的解密密钥标识符；无记录时为 <see langword="null"/>。</para></summary>
    /// <value>The key identifier, or <see langword="null"/>. <para>密钥标识符，或 <see langword="null"/>。</para></value>
    public string? KeyId { get; init; }
}