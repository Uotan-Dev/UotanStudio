namespace UotanStudio.Compress;

/// <summary>
/// Identifies a compression container format handled by the compression services.
/// <para>标识压缩服务所处理的压缩容器格式。</para>
/// </summary>
public enum CompressionFormat
{
    /// <summary>No compression or an unrecognized format. <para>未压缩或无法识别的格式。</para></summary>
    None = 0,

    /// <summary>gzip stream, detected by the <c>0x1F 0x8B</c> magic. <para>gzip 流，依据 <c>0x1F 0x8B</c> 魔数识别。</para></summary>
    Gzip = 1,

    /// <summary>zlib wrapper around a DEFLATE stream; no magic bytes are registered. <para>DEFLATE 流外层的 zlib 封装；未注册魔数字节。</para></summary>
    Zlib = 2,

    /// <summary>Raw DEFLATE stream without a container header. <para>不含容器头的原始 DEFLATE 流。</para></summary>
    Deflate = 3,

    /// <summary>Brotli stream, typically using the <c>.br</c> extension. <para>Brotli 流，通常使用 <c>.br</c> 扩展名。</para></summary>
    Brotli = 4,

    /// <summary>LZ4 frame format, detected by the <c>0x04 0x22 0x4D 0x18</c> magic. <para>LZ4 帧格式，依据 <c>0x04 0x22 0x4D 0x18</c> 魔数识别。</para></summary>
    Lz4 = 5,

    /// <summary>Legacy LZ4 block format, detected by the <c>0x02 0x21 0x4C 0x18</c> magic. <para>传统 LZ4 块格式，依据 <c>0x02 0x21 0x4C 0x18</c> 魔数识别。</para></summary>
    Lz4Legacy = 6,

    /// <summary>LZMA stream; decoding buffers the whole result. <para>LZMA 流；解码会完整缓冲结果。</para></summary>
    Lzma = 7,

    /// <summary>xz container, detected by its magic; decoding buffers the whole result. <para>xz 容器，依据魔数识别；解码会完整缓冲结果。</para></summary>
    Xz = 8,

    /// <summary>bzip2 stream, detected by the <c>BZh</c> magic. <para>bzip2 流，依据 <c>BZh</c> 魔数识别。</para></summary>
    Bzip2 = 9,

    /// <summary>Zstandard format, detected by its magic; decoding buffers the whole result. <para>Zstandard 格式，依据魔数识别；解码会完整缓冲结果。</para></summary>
    Zstd = 10,

    /// <summary>LZOP container, detected by its magic; decoding buffers the whole result. <para>LZOP 容器，依据魔数识别；解码会完整缓冲结果。</para></summary>
    Lzop = 11,

    /// <summary>Zopfli-produced gzip output; registered for decompression only. <para>Zopfli 生成的 gzip 输出；仅注册用于解压。</para></summary>
    Zopfli = 12
}