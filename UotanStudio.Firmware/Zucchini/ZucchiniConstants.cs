namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// Defines the constants of the Zucchini binary delta patch format (RAW subset).
/// <para>定义 Zucchini 二进制差分补丁格式（RAW 子集）的常量。</para>
/// </summary>
public static class ZucchiniConstants
{
    /// <summary>
    /// The 32-bit magic value stored little-endian in the first four bytes of a Zucchini patch.
    /// The byte sequence spells <c>"ZcP\x03"</c>, matching the upstream Chromium/AOSP zucchini
    /// <c>kMagic</c> constant. Because this applier has no upstream sample to cross-verify against,
    /// both the applier and the test-side writer use this same constant, making the tests a
    /// self-consistent round-trip validation; real-patch interoperability is documented as unverified.
    /// <para>Zucchini 补丁前四字节以 little-endian 存储的 32 位魔数，字节序列即
    /// <c>"ZcP\x03"</c>，与上游 Chromium/AOSP zucchini 的 <c>kMagic</c> 常量一致。由于本应用器
    /// 没有上游样本可交叉核对，应用器与测试侧写入器共用该常量，测试即为自洽的回环验证；
    /// 与真实补丁的互通性标记为未验证。</para>
    /// </summary>
    public const uint Magic = 0x0350635A;

    /// <summary>
    /// The supported patch format version.
    /// <para>受支持的补丁格式版本号。</para>
    /// </summary>
    public const uint Version = 1;

    /// <summary>
    /// The size in bytes of the fixed patch header (7 little-endian uint32 fields).
    /// <para>定长补丁头部的字节数（7 个 little-endian uint32 字段）。</para>
    /// </summary>
    public const int HeaderByteSize = 7 * sizeof(uint);

    /// <summary>
    /// The size in bytes of the fixed per-element header (10 little-endian uint32 fields:
    /// old offset/size/exe_type, new offset/size/exe_type, and four data-stream lengths).
    /// <para>定长元素头部的字节数（10 个 little-endian uint32 字段：
    /// 旧 offset/size/exe_type、新 offset/size/exe_type 及四个数据流长度）。</para>
    /// </summary>
    public const int ElementHeaderByteSize = 10 * sizeof(uint);
}