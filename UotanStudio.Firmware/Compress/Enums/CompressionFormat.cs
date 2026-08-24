namespace UotanStudio.Firmware.Compress;

/// <summary>
/// 压缩格式类型枚举。
/// <para>Enumeration of compression format types.</para>
/// <para>定义了库支持的各种压缩格式，用于压缩、解压和自动检测的统一管理。</para>
/// <para>Defines the various compression formats supported by the library, used for unified management of compression, decompression and automatic detection.</para>
/// </summary>
public enum CompressionFormat
{
    /// <summary>
    /// 无压缩或未知格式。
    /// <para>No compression or unknown format.</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// GZIP 压缩格式（RFC 1952）。
    /// <para>GZIP compression format (RFC 1952).</para>
    /// </summary>
    Gzip = 1,

    /// <summary>
    /// ZLIB 压缩格式（RFC 1950）。
    /// <para>ZLIB compression format (RFC 1950).</para>
    /// </summary>
    Zlib = 2,

    /// <summary>
    /// 原始 DEFLATE 压缩格式（RFC 1951）。
    /// <para>Raw DEFLATE compression format (RFC 1951).</para>
    /// </summary>
    Deflate = 3,

    /// <summary>
    /// Brotli 压缩格式。
    /// <para>Brotli compression format.</para>
    /// </summary>
    Brotli = 4,

    /// <summary>
    /// LZ4 压缩格式（标准帧）。
    /// <para>LZ4 compression format (standard frame).</para>
    /// </summary>
    Lz4 = 5,

    /// <summary>
    /// LZ4 传统压缩格式（magiskboot 风格块帧）。
    /// <para>LZ4 legacy compression format (magiskboot-style block framing).</para>
    /// </summary>
    Lz4Legacy = 6,

    /// <summary>
    /// LZMA 压缩格式（带 .lzma 头：属性字节 + 字典大小 + 未压缩大小）。
    /// <para>LZMA compression format (with .lzma header: property byte + dictionary size + uncompressed size).</para>
    /// </summary>
    Lzma = 7,

    /// <summary>
    /// XZ (LZMA2) 压缩格式。
    /// <para>XZ (LZMA2) compression format.</para>
    /// </summary>
    Xz = 8,

    /// <summary>
    /// BZIP2 压缩格式。
    /// <para>BZIP2 compression format.</para>
    /// </summary>
    Bzip2 = 9,

    /// <summary>
    /// Zstandard (zstd) 压缩格式。
    /// <para>Zstandard (zstd) compression format.</para>
    /// </summary>
    Zstd = 10,

    /// <summary>
    /// LZOP 压缩格式（当前版本仅支持魔数识别，不提供压缩/解压实现）。
    /// <para>LZOP compression format (this version only recognizes the magic; compression/decompression are not implemented).</para>
    /// </summary>
    Lzop = 11,

    /// <summary>
    /// Zopfli 压缩格式（GZIP 兼容输出；当前版本明确标注不支持）。
    /// <para>Zopfli compression format (GZIP-compatible output; explicitly unsupported in this version).</para>
    /// </summary>
    Zopfli = 12
}