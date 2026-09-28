namespace UotanStudio.Firmware.VendorFirmware.Rockchip;

/// <summary>
/// Header identity, version, timestamp and template-reuse settings for Rockchip packing.
/// <para>瑞芯微打包时使用的头部标识、版本、时间戳与模板复用设置。</para>
/// </summary>
public sealed class RockchipPackOptions
{
    /// <summary>Rockchip chip family name encoded into the RKFW loader-id bytes.
    /// <para>编码进 RKFW 加载器标识字节的瑞芯微芯片系列名称。</para></summary>
    /// <value>A name such as <c>RK3588</c>, <c>RK3566</c> or <c>RV1126</c>; <see langword="null"/> uses
    /// <c>RK3566</c> for a fresh header and keeps the template bytes otherwise. An unsupported name is rejected
    /// during packing.
    /// <para>形如 <c>RK3588</c>、<c>RK3566</c>、<c>RV1126</c> 的名称；为 <see langword="null"/> 时全新头部使用
    /// <c>RK3566</c>，复用模板时保留模板字节。不支持的名称会在打包时被拒绝。</para></value>
    public string? Chip { get; init; }

    /// <summary>Version string written to the RKFW header.
    /// <para>写入 RKFW 头部的版本字符串。</para></summary>
    /// <value><c>major.minor.build</c> with byte, byte and ushort components; <see langword="null"/> uses
    /// <c>1.0.0</c> for a fresh header and keeps the template bytes otherwise.
    /// <para>格式为 <c>major.minor.build</c>，分别按 byte、byte、ushort 解析；为 <see langword="null"/> 时全新头部
    /// 使用 <c>1.0.0</c>，复用模板时保留模板字节。</para></value>
    public string? Version { get; init; }

    /// <summary>Release timestamp written to the RKFW header.
    /// <para>写入 RKFW 头部的发布时间戳。</para></summary>
    /// <value>Converted to UTC before encoding year, month, day and time; <see langword="null"/> uses the Unix
    /// epoch for a fresh header and keeps the template bytes otherwise.
    /// <para>编码前会转换为 UTC 的年月日与时分秒；为 <see langword="null"/> 时全新头部使用 Unix 纪元，
    /// 复用模板时保留模板字节。</para></value>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>32-bit detection code written little-endian into the RKFW header.
    /// <para>以小端序写入 RKFW 头部的 32 位检测码。</para></summary>
    /// <value><see langword="null"/> uses <c>0x02000000</c> for a fresh header and keeps the template value
    /// otherwise.
    /// <para>为 <see langword="null"/> 时全新头部使用 <c>0x02000000</c>，复用模板时保留模板值。</para></value>
    public uint? Code { get; init; }

    /// <summary>Model identity written to the RKAF header.
    /// <para>写入 RKAF 头部的机型标识。</para></summary>
    /// <value>UTF-8, NUL-terminated text for the 34-byte model field; <see langword="null"/> reads the template
    /// value or falls back to <c>UotanStudio</c>.
    /// <para>写入 34 字节机型字段的以 NUL 结尾的 UTF-8 文本；为 <see langword="null"/> 时读取模板值，
    /// 否则回退为 <c>UotanStudio</c>。</para></value>
    public string? Model { get; init; }

    /// <summary>Manufacturer identity written to the RKAF header.
    /// <para>写入 RKAF 头部的厂商标识。</para></summary>
    /// <value>UTF-8, NUL-terminated text for the 56-byte manufacturer field; <see langword="null"/> reads the
    /// template value or falls back to <c>UotanStudio</c>.
    /// <para>写入 56 字节厂商字段的以 NUL 结尾的 UTF-8 文本；为 <see langword="null"/> 时读取模板值，
    /// 否则回退为 <c>UotanStudio</c>。</para></value>
    public string? Manufacturer { get; init; }

    /// <summary>Whether a header template in the source directory seeds the written header.
    /// <para>是否使用源目录中的头部模板作为写出头部的初始内容。</para></summary>
    /// <value>When <see langword="true"/> (the default) and <c>rkaf-header.bin</c> or <c>rkfw-header.bin</c>
    /// exists with the matching magic, its bytes are reused and only the fields this run computes are
    /// overwritten; a missing template silently builds a fresh header.
    /// <para>为 <see langword="true"/>（默认值）且源目录存在魔数匹配的 <c>rkaf-header.bin</c> 或
    /// <c>rkfw-header.bin</c> 时，复用其字节并仅覆写本次计算得到的字段；模板缺失时静默生成全新头部。</para></value>
    public bool ReuseHeaderTemplate { get; init; } = true;
}