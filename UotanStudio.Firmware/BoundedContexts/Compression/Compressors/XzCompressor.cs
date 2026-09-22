using SharpCompress.Compressors.Xz;
using UotanStudio.Firmware.BoundedContexts.Compression.Internal;
using UotanStudio.Firmware.BoundedContexts.Compression.Internal.Xz;

namespace UotanStudio.Firmware.BoundedContexts.Compression.Compressors;

public static class XzCompressor
{
    private static readonly byte[] Lzma2EosMarker = [0x00];

    /// <summary>
    /// XZ compression.
    /// <para>XZ 压缩。</para>
    /// </summary>
    public static byte[] Compress(byte[] data, uint dictionarySize = Lzma2Encoder.DefaultDictionarySize, byte checkType = XzContainer.CheckTypeCrc32, int? maxDegreeOfParallelism = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        try
        {
            if (data.Length == 0)
            {
                return XzContainer.Wrap(Lzma2EosMarker, data, dictionarySize, checkType);
            }

            var lzma2 = Lzma2Encoder.Encode(data, dictionarySize, maxDegreeOfParallelism);
            return XzContainer.Wrap(lzma2, data, dictionarySize, checkType);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 压缩失败", ex);
        }
    }

    /// <summary>
    /// Compresses bounded 64 KiB input windows, spooling compressed data before writing the container.
    /// <para>按有界 64 KiB 窗口压缩输入，将压缩数据暂存后写出容器。</para>
    /// </summary>
    public static void Compress(Stream input, Stream output, uint dictionarySize = Lzma2Encoder.DefaultDictionarySize,
        byte checkType = XzContainer.CheckTypeCrc32, int? maxDegreeOfParallelism = null)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        try
        {
            using var lzma2 = new FileStream(Path.Combine(Path.GetTempPath(), $"uotan-xz-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);

            var crc64 = new XzCrc64();
            var crc32 = new System.IO.Hashing.Crc32();

            var total = Lzma2Encoder.Encode(input, lzma2, dictionarySize, maxDegreeOfParallelism,
                (win, offset, count) =>
                {
                    if (checkType == XzContainer.CheckTypeCrc64)
                    {
                        crc64.Append(new ReadOnlySpan<byte>(win, offset, count));
                    }
                    else
                    {
                        crc32.Append(new ReadOnlySpan<byte>(win, offset, count));
                    }
                });

            byte[] check;
            if (checkType == XzContainer.CheckTypeCrc64)
            {
                var crc = crc64.GetCurrentHashAsUInt64();
                check = new byte[8];
                for (var i = 0; i < 8; i++)
                {
                    check[i] = (byte)((crc >> (8 * i)) & 0xFF);
                }
            }
            else
            {
                var crc = crc32.GetCurrentHashAsUInt32();
                check = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    check[i] = (byte)((crc >> (8 * i)) & 0xFF);
                }
            }

            var compressedSize = lzma2.Length;
            lzma2.Position = 0;
            XzContainer.WriteContainer(output, lzma2, compressedSize, (ulong)total, check, dictionarySize, checkType);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 压缩失败", ex);
        }
    }

    /// <summary>
    /// XZ decompression.
    /// <para>XZ 解压。</para>
    /// </summary>
    public static byte[] Decompress(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            Decompress(input, output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 解压失败", ex);
        }
    }

    /// <summary>
    /// Streaming XZ decompression: the underlying XZStream is inherently streaming, so large inputs are piped without full buffering.
    /// <para>流式 XZ 解压：底层 XZStream 本身是流式的，无需整块缓冲，适合大输入。</para>
    /// </summary>
    public static void Decompress(Stream input, Stream output)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        try
        {
            using var xzStream = new XZStream(new NonDisposingStream(input));
            xzStream.CopyTo(output);
        }
        catch (Exception ex)
        {
            throw new CompressionException("XZ 解压失败", ex);
        }
    }

    /// <summary>
    /// Detects whether the data is in XZ format (magic FD 37 7A 58 5A 00).
    /// <para>检测数据是否为 XZ 格式（魔数 FD 37 7A 58 5A 00）。</para>
    /// </summary>
    public static bool IsXzFormat(byte[] data)
    {
        return data.Length >= 6 &&
               data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A &&
               data[3] == 0x58 && data[4] == 0x5A && data[5] == 0x00;
    }
}