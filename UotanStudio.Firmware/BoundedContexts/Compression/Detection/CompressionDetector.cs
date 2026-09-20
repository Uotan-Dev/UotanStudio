namespace UotanStudio.Firmware.BoundedContexts.Compression.Detection;

/// <summary>Detects compression framing from bytes or a stream header without selecting a codec by extension.</summary>
public static class CompressionDetector
{
    private static readonly byte[] MagicGzip = [0x1F, 0x8B];
    private static readonly byte[] MagicZstd = [0x28, 0xB5, 0x2F, 0xFD];
    private static readonly byte[] MagicBzip2 = [0x42, 0x5A, 0x68];
    private static readonly byte[] MagicXz = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static readonly byte[] MagicLzop = [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

    public static CompressionFormat Detect(byte[] data) => data == null ? throw new ArgumentNullException(nameof(data)) : Detect(data.AsSpan());

    public static CompressionFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return CompressionFormat.None;
        }


        if (StartsWith(data, MagicGzip))
        {
            return CompressionFormat.Gzip;
        }

        if (StartsWith(data, MagicZstd))
        {
            return CompressionFormat.Zstd;
        }

        if (StartsWith(data, MagicBzip2))
        {
            return CompressionFormat.Bzip2;
        }

        if (StartsWith(data, MagicXz))
        {
            return CompressionFormat.Xz;
        }

        if (StartsWith(data, MagicLzop))
        {
            return CompressionFormat.Lzop;
        }


        if (data.Length >= 4)
        {
            if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
            {
                return CompressionFormat.Lz4;
            }

            if (data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18)
            {
                return CompressionFormat.Lz4Legacy;
            }
        }


        return IsLzmaFormat(data) ? CompressionFormat.Lzma : IsZlibFormat(data) ? CompressionFormat.Zlib : CompressionFormat.None;
    }

    /// <summary>
    /// Reads the stream head to detect the compression format; restores the position when the stream is seekable.
    /// <para>读取流头部检测压缩格式；若流可定位则恢复原位置。</para>
    /// </summary>
    public static CompressionFormat Detect(Stream input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var buffer = new byte[16];
        var read = 0;
        long? position = input.CanSeek ? input.Position : null;

        while (read < buffer.Length)
        {
            var n = input.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        if (position.HasValue)
        {
            input.Position = position.Value;
        }

        return Detect(buffer.AsSpan(0, read));
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, byte[] magic)
    {
        if (data.Length < magic.Length)
        {
            return false;
        }

        for (var i = 0; i < magic.Length; i++)
        {
            if (data[i] != magic[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Detects whether the data conforms to the LZMA format (heuristic).
    /// <para>检测数据是否符合 LZMA 格式（启发式）。</para>
    /// </summary>
    private static bool IsLzmaFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13)
        {
            return false;
        }

        if (!IsValidLzmaPropertyByte(data[0]))
        {
            return false;
        }

        var dictSize = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
        return dictSize != 0 && (dictSize & (dictSize - 1)) == 0 && dictSize is >= 4096 and <= 0x8000000;
    }

    /// <summary>
    /// Validates the LZMA property byte (lc + 9 * (lp + 5 * pb), lc≤8, lp≤4, pb≤4).
    /// <para>验证 LZMA 属性字节是否有效（lc + 9 * (lp + 5 * pb)，lc≤8，lp≤4，pb≤4）。</para>
    /// </summary>
    private static bool IsValidLzmaPropertyByte(byte propertyByte)
    {
        if (propertyByte > 0xE0)
        {
            return false;
        }

        var pb = propertyByte / (9 * 5);
        var rem = propertyByte % (9 * 5);
        var lp = rem / 9;
        var lc = rem % 9;

        return pb <= 4 && lp <= 4 && lc <= 8;
    }

    /// <summary>
    /// Detects whether the data conforms to the ZLIB format (heuristic: CM=8 and (CMF*256+FLG) % 31 == 0).
    /// <para>检测数据是否符合 ZLIB 格式（启发式：CM=8 且 CMF/FLG 可被 31 整除）。</para>
    /// </summary>
    private static bool IsZlibFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        var cmf = data[0];
        var flg = data[1];

        return (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0;
    }

}