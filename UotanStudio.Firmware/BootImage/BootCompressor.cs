using System.IO.Compression;
using K4os.Compression.LZ4.Streams;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using ZstdSharp;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// 启动镜像压缩格式检测与解压的内部工具。
/// <para>
/// Internal compression detection and decompression utility for boot images.</para>
/// <para>
/// 依据魔数自动识别 gzip / lz4 / lzma / xz / bzip2 / zstd / brotli 等格式，
/// 并借助 BCL 与已配置压缩包完成解压（替代原 FirmwareKit.Compress 依赖）。</para>
/// <para>
/// Detects gzip / lz4 / lzma / xz / bzip2 / zstd / brotli by magic bytes and decompresses
/// using the BCL and configured packages (replacing the former FirmwareKit.Compress dependency).</para>
/// </summary>
internal static class BootCompressor
{
    /// <summary>
    /// 依据魔数检测数据的压缩格式。
    /// <para>Detects the compression format of data by magic bytes.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to inspect.</para></param>
    /// <returns>压缩类型；无法识别时返回 <see cref="CompressionType.None"/>。
    /// <para>The compression type, or <see cref="CompressionType.None"/> when unrecognized.</para></returns>
    public static CompressionType Detect(byte[] data)
    {
        if (data is null || data.Length < 4)
        {
            return CompressionType.None;
        }

        // gzip / zopfli 兼容
        if (data[0] == 0x1F && data[1] == 0x8B)
        {
            return CompressionType.Gzip;
        }

        // lz4 legacy magic (0x02 0x21 0x4C 0x18)
        if (data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18)
        {
            return CompressionType.Lz4Legacy;
        }

        // lz4 standard frame magic (0x04 0x22 0x4D 0x18)
        if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
        {
            return CompressionType.Lz4;
        }

        // xz magic
        if (data.Length >= 6 && data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A
            && data[3] == 0x58 && data[4] == 0x5A && data[5] == 0x00)
        {
            return CompressionType.Xz;
        }

        // bzip2 magic
        if (data[0] == (byte)'B' && data[1] == (byte)'Z' && data[2] == (byte)'h')
        {
            return CompressionType.Bzip2;
        }

        // zstd magic
        if (data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
        {
            return CompressionType.Zstd;
        }

        // lzop magic (0x89 0x4C 0x5A 0x4F ...)
        if (data[0] == 0x89 && data[1] == 0x4C && data[2] == 0x5A && data[3] == 0x4F)
        {
            return CompressionType.Lzop;
        }

        // brotli magic
        if (data[0] == 0x0B && data[1] == 0x0A)
        {
            return CompressionType.Brotli;
        }

        // lzma alone (5-byte properties + 8-byte uncompressed size) 常见于 Android boot
        return data[0] == 0x5D && data[1] == 0x00 && data[2] == 0x00 ? CompressionType.Lzma : CompressionType.None;
    }

    /// <summary>
    /// 解压数据；失败或格式不受支持时返回 null。
    /// <para>Decompresses data, or returns null when unsupported or on failure.</para>
    /// </summary>
    /// <param name="data">压缩数据。<para>The compressed data.</para></param>
    /// <param name="type">压缩类型（可先经 <see cref="Detect"/> 获得）。
    /// <para>The compression type (e.g. from <see cref="Detect"/>).</para></param>
    /// <returns>解压后的数据；无法解压时返回 null。
    /// <para>The decompressed data, or null when decompression is impossible.</para></returns>
    public static byte[]? TryDecompress(byte[] data, CompressionType type)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();

            switch (type)
            {
                case CompressionType.Gzip:
                case CompressionType.Zopfli:
                    using (var gz = new GZipStream(input, CompressionMode.Decompress))
                    {
                        gz.CopyTo(output);
                    }

                    break;
                case CompressionType.Bzip2:
                    using (var bz = BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, false))
                    {
                        bz.CopyTo(output);
                    }

                    break;
                case CompressionType.Xz:
                    using (var xz = new XZStream(input))
                    {
                        xz.CopyTo(output);
                    }

                    break;
                case CompressionType.Zstd:
                    using (var zstd = new Decompressor())
                    {
                        var raw = zstd.Unwrap(data.AsSpan()).ToArray();
                        return raw;
                    }
                case CompressionType.Brotli:
                    using (var br = new BrotliStream(input, CompressionMode.Decompress))
                    {
                        br.CopyTo(output);
                    }

                    break;
                case CompressionType.Lz4:
                    using (var lz4 = LZ4Stream.Decode(input, leaveOpen: false))
                    {
                        lz4.CopyTo(output);
                    }

                    break;
                case CompressionType.None:
                    break;
                case CompressionType.Lz4Legacy:
                    break;
                case CompressionType.Lz4Lg:
                    break;
                case CompressionType.Lzma:
                    break;
                case CompressionType.Lzop:
                    break;
                // LZ4 legacy / LZMA / LZOP 解压未内联实现（原依赖 FirmwareKit.Compress）。
                // LZ4 legacy, LZMA and LZOP decompression are not inlined here
                // (the original implementation relied on FirmwareKit.Compress).
                default:
                    return null;
            }

            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 便捷重载：自动检测并尝试解压。
    /// <para>Convenience overload: auto-detects and tries to decompress.</para>
    /// </summary>
    /// <param name="data">压缩数据。<para>The compressed data.</para></param>
    /// <returns>解压后的数据；无法识别或解压失败时返回原数据。
    /// <para>The decompressed data, or the original data when unrecognized or failing.</para></returns>
    public static byte[] DecompressOrReturn(byte[] data)
    {
        var type = Detect(data);
        return type == CompressionType.None ? data : TryDecompress(data, type) ?? data;
    }
}