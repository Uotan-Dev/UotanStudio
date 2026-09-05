namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// 压缩格式类型枚举。
/// <para>Enumeration of compression format types.</para>
/// <para>定义了 magiskboot 支持的各种压缩格式，用于解包和重打包时的自动检测和处理。</para>
/// <para>Defines the various compression formats supported by magiskboot, used for automatic detection and processing during unpacking and repacking.</para>
/// </summary>
public enum CompressionType
{
    /// <summary>
    /// 无压缩或未知格式。
    /// <para>No compression or unknown format.</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// GZIP 压缩格式。
    /// <para>GZIP compression format.</para>
    /// </summary>
    Gzip = 1,

    /// <summary>
    /// LZ4 压缩格式（标准帧格式）。
    /// <para>LZ4 compression format (standard frame).</para>
    /// </summary>
    Lz4 = 2,

    /// <summary>
    /// LZ4 传统压缩格式。
    /// <para>LZ4 legacy compression format.</para>
    /// </summary>
    Lz4Legacy = 3,

    /// <summary>
    /// LZ4 LG 特定压缩格式（仅用于 LG 设备）。
    /// <para>LZ4 LG-specific compression format (used only for LG devices).</para>
    /// </summary>
    Lz4Lg = 4,

    /// <summary>
    /// LZMA 压缩格式。
    /// <para>LZMA compression format.</para>
    /// </summary>
    Lzma = 5,

    /// <summary>
    /// XZ (LZMA2) 压缩格式。
    /// <para>XZ (LZMA2) compression format.</para>
    /// </summary>
    Xz = 6,

    /// <summary>
    /// BZIP2 压缩格式。
    /// <para>BZIP2 compression format.</para>
    /// </summary>
    Bzip2 = 7,

    /// <summary>
    /// Zopfli 压缩格式（GZIP 兼容）。
    /// <para>Zopfli compression format (GZIP-compatible).</para>
    /// </summary>
    Zopfli = 8,

    /// <summary>
    /// LZOP 压缩格式。
    /// <para>LZOP compression format.</para>
    /// </summary>
    Lzop = 9,

    /// <summary>
    /// Zstandard 压缩格式。
    /// <para>Zstandard compression format.</para>
    /// </summary>
    Zstd = 10,

    /// <summary>
    /// Brotli 压缩格式。
    /// <para>Brotli compression format.</para>
    /// </summary>
    Brotli = 11
}