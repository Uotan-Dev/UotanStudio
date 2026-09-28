namespace UotanStudio.Firmware.BootSecurity.BootImage.Models;

/// <summary>
/// Compression formats that a boot image component can be labelled with, using the numeric ids serialized in boot image headers.
/// <para>引导镜像组件可标注的压缩格式，其数值即引导镜像头部中序列化的类型 id。</para>
/// </summary>
public enum CompressionType
{
    /// <summary>Component is stored uncompressed. <para>组件以未压缩形式存储。</para></summary>
    None = 0,

    /// <summary>Gzip (DEFLATE) compressed stream. <para>Gzip（DEFLATE）压缩流。</para></summary>
    Gzip = 1,

    /// <summary>LZ4 frame-format stream. <para>LZ4 帧格式流。</para></summary>
    Lz4 = 2,

    /// <summary>Legacy LZ4 format identified by the legacy frame magic. <para>以传统帧魔数识别的传统 LZ4 格式。</para></summary>
    Lz4Legacy = 3,

    /// <summary>LZ4LG container variant. <para>LZ4LG 容器变体。</para></summary>
    Lz4Lg = 4,

    /// <summary>LZMA compressed stream. <para>LZMA 压缩流。</para></summary>
    Lzma = 5,

    /// <summary>XZ compressed stream. <para>XZ 压缩流。</para></summary>
    Xz = 6,

    /// <summary>BZip2 compressed stream. <para>BZip2 压缩流。</para></summary>
    Bzip2 = 7,

    /// <summary>Zopfli-produced DEFLATE/gzip stream. <para>Zopfli 生成的 DEFLATE/gzip 流。</para></summary>
    Zopfli = 8,

    /// <summary>LZO compressed stream. <para>LZO 压缩流。</para></summary>
    Lzop = 9,

    /// <summary>Zstandard compressed stream. <para>Zstandard 压缩流。</para></summary>
    Zstd = 10,

    /// <summary>Brotli compressed stream. <para>Brotli 压缩流。</para></summary>
    Brotli = 11
}