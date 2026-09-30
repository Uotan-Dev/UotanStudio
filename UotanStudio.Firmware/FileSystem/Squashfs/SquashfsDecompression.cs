using K4os.Compression.LZ4;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Codecs.Lzo;
using UotanStudio.Firmware.Compression.Compressors;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.FileSystem.Squashfs;

/// <summary>
/// Per-block decompression for the SquashFS v4 compressor ids, wired to the repository codecs. Each call
/// decompresses one independent block and refuses any output larger than the expected size.
/// <para>SquashFS v4 压缩器标识的逐块解压，接线到仓库内的编解码器。每次调用解压一个独立块，并拒绝超出
/// 预期大小的输出。</para>
/// </summary>
internal static class SquashfsDecompression
{
    /// <summary>
    /// Decompresses one SquashFS metadata or data block.
    /// <para>解压一个 SquashFS 元数据或数据块。</para>
    /// </summary>
    /// <param name="compression">Superblock compressor id (1 gzip, 3 lzo, 4 xz, 5 lz4, 6 zstd).
    /// <para>超级块压缩器标识（1 gzip、3 lzo、4 xz、5 lz4、6 zstd）。</para></param>
    /// <param name="payload">Raw block bytes. <para>块的原始字节。</para></param>
    /// <param name="expectedSize">Exact or maximum uncompressed size; growth beyond it aborts.
    /// <para>精确或最大的未压缩大小；超出即中止。</para></param>
    /// <param name="exactOutput">When <see langword="true"/> the decoded length must equal
    /// <paramref name="expectedSize"/>. <para>为 <see langword="true"/> 时输出长度必须等于
    /// <paramref name="expectedSize"/>。</para></param>
    /// <returns>The decoded bytes, at most <paramref name="expectedSize"/> in length.
    /// <para>解码后的字节，长度不超过 <paramref name="expectedSize"/>。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">The compressor id is unsupported.
    /// <para>压缩器标识不受支持时抛出。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The payload is corrupt.
    /// <para>载荷损坏时抛出。</para></exception>
    internal static byte[] Decompress(int compression, byte[] payload, int expectedSize, bool exactOutput)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        return compression switch
        {
            1 => DecompressZlib(payload, expectedSize, exactOutput),
            3 => DecompressLzo(payload, expectedSize, exactOutput),
            4 => DecompressXz(payload, expectedSize, exactOutput),
            5 => DecompressLz4(payload, expectedSize, exactOutput),
            6 => DecompressZstd(payload, expectedSize, exactOutput),
            2 => throw new FileSystemUnsupportedFeatureException("SquashFS compressor 'lzma' (id 2) is not supported because it uses a legacy MicroLZMA-style header without kernel support."),
            _ => throw new FileSystemUnsupportedFeatureException($"SquashFS compressor id {compression} is not supported."),
        };
    }

    /// <summary>Maps a superblock compressor id to its display name.
    /// <para>将超级块压缩器标识映射为显示名称。</para></summary>
    /// <param name="compression">The raw compressor id. <para>原始压缩器标识。</para></param>
    /// <returns>A human-readable name; unknown ids render as <c>id (n)</c>.
    /// <para>人类可读名称；未知标识渲染为 <c>id (n)</c>。</para></returns>
    internal static string Describe(int compression) => compression switch
    {
        0 => "none",
        1 => "gzip",
        2 => "lzma",
        3 => "lzo",
        4 => "xz",
        5 => "lz4",
        6 => "zstd",
        _ => $"id ({compression})",
    };

    private static byte[] DecompressZlib(byte[] payload, int expectedSize, bool exactOutput)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var output = new MemoryStream();
        using var bounded = new CountingWriteStream(output, expectedSize, leaveOpen: true);
        try
        {
            ZlibCompressor.Decompress(input, bounded);
        }
        catch (CompressionException ex)
        {
            throw new FileSystemCorruptedException("The SquashFS gzip block is corrupt.", ex);
        }
        catch (IOException ex)
        {
            throw new FileSystemCorruptedException($"The SquashFS gzip block expands beyond the expected {expectedSize} bytes.", ex);
        }

        return Finish(output.Length, exactOutput, expectedSize, () => output.ToArray());
    }

    private static byte[] DecompressLzo(byte[] payload, int expectedSize, bool exactOutput)
    {
        var buffer = new byte[expectedSize];
        var status = Lzo1x.TryDecompress(payload, buffer, out var written);
        if (status != LzoStatus.Ok)
        {
            throw new FileSystemCorruptedException($"The SquashFS LZO block failed to decode ({status}).");
        }

        if (exactOutput && written != expectedSize)
        {
            throw new FileSystemCorruptedException($"The SquashFS LZO block produced {written} bytes but {expectedSize} were expected.");
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    private static byte[] DecompressXz(byte[] payload, int expectedSize, bool exactOutput)
    {
        if (payload.Length < 6 || !XzCompressor.IsXzFormat(payload))
        {
            throw new FileSystemUnsupportedFeatureException(
                "SquashFS xz blocks are only supported in full .xz container framing; raw LZMA2 framing is not decoded.");
        }

        using var input = new MemoryStream(payload, writable: false);
        using var output = new MemoryStream();
        using var bounded = new CountingWriteStream(output, expectedSize, leaveOpen: true);
        try
        {
            XzCompressor.Decompress(input, bounded);
        }
        catch (CompressionException ex)
        {
            throw new FileSystemCorruptedException("The SquashFS xz block is corrupt.", ex);
        }
        catch (IOException ex)
        {
            throw new FileSystemCorruptedException($"The SquashFS xz block expands beyond the expected {expectedSize} bytes.", ex);
        }

        return Finish(output.Length, exactOutput, expectedSize, () => output.ToArray());
    }

    private static byte[] DecompressLz4(byte[] payload, int expectedSize, bool exactOutput)
    {
        var buffer = new byte[expectedSize];
        var written = LZ4Codec.Decode(payload, 0, payload.Length, buffer, 0, buffer.Length);
        if (written < 0)
        {
            throw new FileSystemCorruptedException(
                exactOutput
                    ? $"The SquashFS LZ4 block decodes larger than the expected {expectedSize} bytes."
                    : "The SquashFS LZ4 block is corrupt.");
        }

        if (exactOutput && written != expectedSize)
        {
            throw new FileSystemCorruptedException($"The SquashFS LZ4 block produced {written} bytes but {expectedSize} were expected.");
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    private static byte[] DecompressZstd(byte[] payload, int expectedSize, bool exactOutput)
    {
        if (payload.Length < 4 || !ZstdCompressor.IsZstdFormat(payload))
        {
            throw new FileSystemCorruptedException("The SquashFS zstd block does not start with a zstd frame magic.");
        }

        using var input = new MemoryStream(payload, writable: false);
        using var output = new MemoryStream();
        using var bounded = new CountingWriteStream(output, expectedSize, leaveOpen: true);
        try
        {
            ZstdCompressor.Decompress(input, bounded);
        }
        catch (CompressionException ex)
        {
            throw new FileSystemCorruptedException("The SquashFS zstd block is corrupt.", ex);
        }
        catch (IOException ex)
        {
            throw new FileSystemCorruptedException($"The SquashFS zstd block expands beyond the expected {expectedSize} bytes.", ex);
        }

        return Finish(output.Length, exactOutput, expectedSize, () => output.ToArray());
    }

    private static byte[] Finish(long length, bool exactOutput, int expectedSize, Func<byte[]> snapshot)
    {
        if (exactOutput && length != expectedSize)
        {
            throw new FileSystemCorruptedException($"A SquashFS block produced {length} bytes but {expectedSize} were expected.");
        }

        return snapshot();
    }
}