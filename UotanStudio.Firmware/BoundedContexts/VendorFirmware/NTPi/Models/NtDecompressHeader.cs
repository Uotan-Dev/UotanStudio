using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

/// <summary>
/// Decoded <c>NTDECOMP</c> header that precedes an LZMA payload inside an NTPI encoded block. The serialized
/// form is a fixed 112-byte record with little-endian integers: magic (offset 0, 8 bytes), primary type (8, 4),
/// decompress subtype (12, 4), padding (16, 8), processed size (24, 8), original size (32, 8) and a trailing
/// pad (40, 72 bytes).
/// <para>解码后的 <c>NTDECOMP</c> 头部，位于 NTPI 编码块中 LZMA 载荷之前。其序列化形式是固定 112 字节的记录，
/// 整数均为小端序：魔数（偏移 0，8 字节）、主类型（8，4）、解压子类型（12，4）、填充（16，8）、
/// 处理后长度（24，8）、原始长度（32，8）以及尾部填充（40，72 字节）。</para>
/// </summary>
public struct NtDecompressHeader
{
    /// <summary>
    /// Magic bytes: a valid header holds exactly the eight ASCII characters <c>NTDECOMP</c>.
    /// <para>魔数字节：有效头部恰好包含 <c>NTDECOMP</c> 这 8 个 ASCII 字符。</para>
    /// </summary>
    public byte[] Magic;

    /// <summary>
    /// Shared ASCII bytes of the <c>NTDECOMP</c> magic that <see cref="IsValid"/> compares against.
    /// <para><see cref="IsValid"/> 比对所用的 <c>NTDECOMP</c> 魔数字节，所有实例共享同一数组。</para>
    /// </summary>
    public static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("NTDECOMP");

    /// <summary>
    /// Codec family stored at offset 8; the NTPI writer emits <c>1</c>, and decode paths read the value
    /// without branching on it.
    /// <para>偏移 8 处的编码族标识；NTPI 打包器写入 <c>1</c>，解码路径读出该值但不据此分支。</para>
    /// </summary>
    public uint PrimaryType;

    /// <summary>
    /// Compression subtype at offset 12: <c>2</c> selects the LZMA1 decode path, any other value the LZMA2 path.
    /// <para>偏移 12 处的压缩子类型：<c>2</c> 选择 LZMA1 解码路径，其他值走 LZMA2 路径。</para>
    /// </summary>
    public uint DecompressSubtype;

    /// <summary>
    /// Reserved eight-byte field at offset 16; parsing fills it in and writing stores <c>0</c>.
    /// <para>偏移 16 处的保留 8 字节字段；解析时会读出，写入时固定为 <c>0</c>。</para>
    /// </summary>
    public ulong Padding;

    /// <summary>
    /// Byte count at offset 24 of the compressed payload following the header, including the five LZMA property
    /// bytes; a value of <c>0</c> makes the decode paths reject the block.
    /// <para>偏移 24 处、头部之后压缩载荷的字节数，包含 5 个 LZMA 属性字节；值为 <c>0</c> 时解码路径会拒绝该块。</para>
    /// </summary>
    public ulong ProcessedSize;

    /// <summary>
    /// Declared plaintext length at offset 32: the exact byte count the decoder must produce, checked against the
    /// caller's output limit before any decompression starts.
    /// <para>偏移 32 处声明的明文长度：解码器必须产出的准确字节数，并在解压开始前与调用方的输出上限比对。</para>
    /// </summary>
    public ulong OriginalSize;

    /// <summary>
    /// Trailing pad at offset 40, seventy-two bytes long; writing stores zeros and no decode path inspects its content.
    /// <para>偏移 40 处的尾部填充，长 72 字节；写入时为全零，解码路径不检查其内容。</para>
    /// </summary>
    public byte[] Padding2;

    /// <summary>
    /// Validates whether the current header represents a valid NTDECOMP decompression header.
    /// <para>验证当前头部是否为有效的 NTDECOMP 解压缩头部。</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when <see cref="Magic"/> is non-null and its bytes equal <see cref="ExpectedMagic"/>
    /// exactly; <see langword="false"/> otherwise.
    /// <para><see cref="Magic"/> 非 <see langword="null"/> 且其字节与 <see cref="ExpectedMagic"/> 完全相同时为
    /// <see langword="true"/>，否则为 <see langword="false"/>。</para>
    /// </returns>
    public readonly bool IsValid() => Magic != null && Magic.AsSpan().SequenceEqual(ExpectedMagic);

    /// <summary>
    /// Gets the string representation of the magic number.
    /// <para>获取魔数的字符串表示。</para>
    /// </summary>
    /// <returns>
    /// The ASCII decoding of <see cref="Magic"/>, or an empty string when it is <see langword="null"/>.
    /// <para><see cref="Magic"/> 的 ASCII 解码结果；其为 <see langword="null"/> 时返回空字符串。</para>
    /// </returns>
    public readonly string GetMagicString() => Magic != null ? Encoding.ASCII.GetString(Magic) : string.Empty;
}