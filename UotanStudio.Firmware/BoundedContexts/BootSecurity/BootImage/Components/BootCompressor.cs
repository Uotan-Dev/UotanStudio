using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using System.IO.Compression;
using K4os.Compression.LZ4.Streams;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using ZstdSharp;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;

public static class BootCompressor
{
    /// <summary>
    /// Detects the compression format of data by magic bytes.
    /// <para>依据魔数检测数据的压缩格式。</para>
    /// </summary>
    public static CompressionType Detect(byte[] data)
    {
        if (data is null || data.Length < 4)
        {
            return CompressionType.None;
        }


        if (data[0] == 0x1F && data[1] == 0x8B)
        {
            return CompressionType.Gzip;
        }


        if (data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18)
        {
            return CompressionType.Lz4Legacy;
        }


        if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
        {
            return CompressionType.Lz4;
        }


        if (data.Length >= 6 && data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A
            && data[3] == 0x58 && data[4] == 0x5A && data[5] == 0x00)
        {
            return CompressionType.Xz;
        }


        if (data[0] == (byte)'B' && data[1] == (byte)'Z' && data[2] == (byte)'h')
        {
            return CompressionType.Bzip2;
        }


        if (data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
        {
            return CompressionType.Zstd;
        }


        if (data[0] == 0x89 && data[1] == 0x4C && data[2] == 0x5A && data[3] == 0x4F)
        {
            return CompressionType.Lzop;
        }


        if (data[0] == 0x0B && data[1] == 0x0A)
        {
            return CompressionType.Brotli;
        }


        return data[0] == 0x5D && data[1] == 0x00 && data[2] == 0x00 ? CompressionType.Lzma : CompressionType.None;
    }

    /// <summary>
    /// Decompresses data, or returns null when unsupported or on failure.
    /// <para>解压数据；失败或格式不受支持时返回 null。</para>
    /// </summary>
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
                    return CompressionService.Decompress(data, CompressionFormat.Lz4Legacy);
                case CompressionType.Lz4Lg:
                    // lz4_lg uses the same legacy block framing in Android boot images.
                    return CompressionService.Decompress(data, CompressionFormat.Lz4Legacy);
                case CompressionType.Lzma:
                    return CompressionService.Decompress(data, CompressionFormat.Lzma);
                case CompressionType.Lzop:
                    return LzopCompressor.Decompress(data);


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
    /// Convenience overload: auto-detects and tries to decompress.
    /// <para>便捷重载：自动检测并尝试解压。</para>
    /// </summary>
    public static byte[] DecompressOrReturn(byte[] data)
    {
        var type = Detect(data);
        return type == CompressionType.None ? data : TryDecompress(data, type) ?? data;
    }
}