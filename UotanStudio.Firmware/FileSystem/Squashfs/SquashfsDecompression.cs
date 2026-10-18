using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Lz4;
using UotanStudio.Compress.Codecs.Lzo;
using UotanStudio.Compress.Compressors;
using ZstdSharp;

namespace UotanStudio.Firmware.FileSystem.Squashfs;

/// <summary>
/// Block decompression for the SquashFS v4 compressor ids, wired to the repository codecs. Each call
/// decompresses one independent block into a caller-provided buffer, so no intermediate array is allocated.
/// <para>SquashFS v4 压缩器标识的逐块解压，接线到仓库内的编解码器。每次调用把一个独立块解压到调用方提供的
/// 缓冲区，因此不会产生中间数组。</para>
/// </summary>
internal static class SquashfsDecompression
{
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
}

/// <summary>
/// Reusable per-worker decoder for one SquashFS compressor. The instance keeps the zstd context alive between
/// blocks, which is the only codec here with reusable state, and writes decoded bytes into a pooled destination
/// buffer bounded by the caller's capacity.
/// <para>单个 SquashFS 压缩器的可复用逐工作者解码器。实例在块之间保留 zstd 上下文（此处唯一具有可复用状态的
/// 编解码器），并把解码字节写入由调用方容量约束的池化目标缓冲。</para>
/// </summary>
internal sealed class SquashfsBlockDecoder : IDisposable
{
    private readonly int _compression;
    private Decompressor? _zstd;

    /// <summary>Creates a decoder for one superblock compressor id.
    /// <para>为单个超级块压缩器标识创建解码器。</para></summary>
    /// <param name="compression">Superblock compressor id (1 gzip, 3 lzo, 4 xz, 5 lz4, 6 zstd).
    /// <para>超级块压缩器标识（1 gzip、3 lzo、4 xz、5 lz4、6 zstd）。</para></param>
    internal SquashfsBlockDecoder(int compression) => _compression = compression;

    /// <summary>
    /// Decompresses one block into <paramref name="destination"/>, whose usable capacity is
    /// <paramref name="capacity"/> bytes.
    /// <para>把一个块解压到 <paramref name="destination"/>，其可用容量为 <paramref name="capacity"/> 字节。</para>
    /// </summary>
    /// <param name="payload">Buffer holding the stored block bytes. <para>承载存储块字节的缓冲区。</para></param>
    /// <param name="payloadLength">Stored bytes in <paramref name="payload"/>.
    /// <para><paramref name="payload"/> 中的存储字节数。</para></param>
    /// <param name="destination">Pooled destination array. <para>池化目标数组。</para></param>
    /// <param name="capacity">Maximum decoded bytes the destination may receive.
    /// <para>目标可接收的最大解码字节数。</para></param>
    /// <param name="exactOutput">When <see langword="true"/> the decoded length must equal
    /// <paramref name="capacity"/>. <para>为 <see langword="true"/> 时解码长度必须等于
    /// <paramref name="capacity"/>。</para></param>
    /// <returns>The decoded byte count. <para>解码后的字节数。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">The compressor id is unsupported.
    /// <para>压缩器标识不受支持时抛出。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The payload is corrupt or expands beyond the capacity.
    /// <para>载荷损坏或展开超出容量时抛出。</para></exception>
    internal int Decompress(byte[] payload, int payloadLength, byte[] destination, int capacity, bool exactOutput)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        var written = _compression switch
        {
            1 => DecompressZlib(payload, payloadLength, destination, capacity),
            3 => DecompressLzo(payload, payloadLength, destination, capacity),
            4 => DecompressXz(payload, payloadLength, destination, capacity),
            5 => DecompressLz4(payload, payloadLength, destination, capacity),
            6 => DecompressZstd(payload, payloadLength, destination, capacity),
            2 => throw new FileSystemUnsupportedFeatureException("SquashFS compressor 'lzma' (id 2) is not supported because it uses a legacy MicroLZMA-style header without kernel support."),
            _ => throw new FileSystemUnsupportedFeatureException($"SquashFS compressor id {_compression} is not supported."),
        };

        if (exactOutput && written != capacity)
        {
            throw new FileSystemCorruptedException($"A SquashFS block produced {written} bytes but {capacity} were expected.");
        }

        return written;
    }

    /// <summary>Releases the reusable codec context. <para>释放可复用的编解码上下文。</para></summary>
    public void Dispose()
    {
        _zstd?.Dispose();
        _zstd = null;
    }

    private static int DecompressZlib(byte[] payload, int payloadLength, byte[] destination, int capacity)
    {
        using var input = new MemoryStream(payload, 0, payloadLength, writable: false);
        var output = new BoundedBufferStream(destination, capacity);
        try
        {
            ZlibCompressor.Decompress(input, output);
        }
        catch (CompressionException ex)
        {
            throw new FileSystemCorruptedException("The SquashFS gzip block is corrupt.", ex);
        }
        catch (IOException ex)
        {
            throw new FileSystemCorruptedException($"The SquashFS gzip block expands beyond the expected {capacity} bytes.", ex);
        }

        return output.Written;
    }

    private static int DecompressLzo(byte[] payload, int payloadLength, byte[] destination, int capacity)
    {
        var status = Lzo1x.TryDecompress(payload.AsSpan(0, payloadLength), destination.AsSpan(0, capacity), out var written);
        if (status != LzoStatus.Ok)
        {
            throw new FileSystemCorruptedException($"The SquashFS LZO block failed to decode ({status}).");
        }

        return written;
    }

    private static int DecompressXz(byte[] payload, int payloadLength, byte[] destination, int capacity)
    {
        if (payloadLength < 6 || !XzCompressor.IsXzFormat(payload))
        {
            throw new FileSystemUnsupportedFeatureException(
                "SquashFS xz blocks are only supported in full .xz container framing; raw LZMA2 framing is not decoded.");
        }

        using var input = new MemoryStream(payload, 0, payloadLength, writable: false);
        var output = new BoundedBufferStream(destination, capacity);
        try
        {
            XzCompressor.Decompress(input, output);
        }
        catch (CompressionException ex)
        {
            throw new FileSystemCorruptedException("The SquashFS xz block is corrupt.", ex);
        }
        catch (IOException ex)
        {
            throw new FileSystemCorruptedException($"The SquashFS xz block expands beyond the expected {capacity} bytes.", ex);
        }

        return output.Written;
    }

    private static int DecompressLz4(byte[] payload, int payloadLength, byte[] destination, int capacity)
    {
        var written = Lz4BlockCodec.Decode(payload.AsSpan(0, payloadLength), destination.AsSpan(0, capacity));
        if (written < 0)
        {
            throw new FileSystemCorruptedException(
                "The SquashFS LZ4 block decodes larger than the expected capacity or is corrupt.");
        }

        return written;
    }

    private int DecompressZstd(byte[] payload, int payloadLength, byte[] destination, int capacity)
    {
        if (payloadLength < 4 || !IsZstdMagic(payload))
        {
            throw new FileSystemCorruptedException("The SquashFS zstd block does not start with a zstd frame magic.");
        }

        _zstd ??= new Decompressor();
        try
        {
            return _zstd.Unwrap(payload.AsSpan(0, payloadLength), destination.AsSpan(0, capacity));
        }
        catch (Exception ex) when (ex is not FileSystemCorruptedException)
        {
            throw new FileSystemCorruptedException("The SquashFS zstd block is corrupt.", ex);
        }
    }

    private static bool IsZstdMagic(byte[] payload) =>
        payload[0] == 0x28 && payload[1] == 0xB5 && payload[2] == 0x2F && payload[3] == 0xFD;

    /// <summary>
    /// Forward-only write sink over a pooled array that rejects any write beyond its capacity, so a decoder
    /// cannot grow the destination.
    /// <para>基于池化数组的仅向前写接收端，拒绝超出容量的写入，使解码器无法扩张目标。</para>
    /// </summary>
    private sealed class BoundedBufferStream(byte[] buffer, int capacity) : Stream
    {
        internal int Written { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> source)
        {
            if (source.Length > capacity - Written)
            {
                throw new IOException($"The SquashFS block expands beyond the {capacity}-byte destination.");
            }

            source.CopyTo(buffer.AsSpan(Written));
            Written += source.Length;
        }

        public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override int Read(byte[] destination, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}