using System.Buffers;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// Seekable read-only view of one IMAGEWTY entry payload. Reads are translated to absolute container
/// offsets and decrypted in complete 16-byte blocks relative to the entry start, which is the same
/// alignment the whole-entry extraction uses, so random-access reads return the same bytes.
/// <para>单个 IMAGEWTY 条目负载的可寻址只读视图。读取会换算为容器绝对偏移，并按相对条目起点的完整 16 字节
/// 分组解密，与整条目提取使用相同对齐，因此随机访问返回相同字节。</para>
/// </summary>
internal sealed class ImageWtyEntryStream : Stream
{
    private const int CipherBlockSize = 16;

    private readonly Stream _container;
    private readonly long _containerOffset;
    private readonly long _storedLength;
    private readonly long _length;
    private readonly bool _decrypt;
    private long _position;

    /// <summary>
    /// Creates a view over one entry.
    /// <para>在单个条目之上创建视图。</para>
    /// </summary>
    /// <param name="container">Container stream; stays open and is shared with other entry views.
    /// <para>容器流；保持打开并与其它条目视图共享。</para></param>
    /// <param name="entry">Entry produced by the container reader. <para>容器读取器产生的条目。</para></param>
    /// <param name="decrypt">Whether entry payloads are RC6-encrypted. <para>条目负载是否经 RC6 加密。</para></param>
    public ImageWtyEntryStream(Stream container, ImageWtyEntry entry, bool decrypt)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(entry);
        _container = container;
        _containerOffset = entry.Offset;
        _storedLength = entry.StoredLength;
        _length = AllwinnerImageWtyFormat.EffectiveLength(entry);
        _decrypt = decrypt;
    }

    /// <summary>Always readable. <para>恒为可读。</para></summary>
    public override bool CanRead => true;

    /// <summary>Always seekable. <para>恒为可寻址。</para></summary>
    public override bool CanSeek => true;

    /// <summary>Never writable. <para>永不可写。</para></summary>
    public override bool CanWrite => false;

    /// <summary>Effective payload length in bytes. <para>有效负载字节长度。</para></summary>
    public override long Length => _length;

    /// <summary>Current logical position, from zero to <see cref="Length"/>.
    /// <para>当前逻辑位置，从 0 到 <see cref="Length"/>。</para></summary>
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>
    /// Reads decrypted logical bytes from the current position.
    /// <para>从当前位置读取解密后的逻辑字节。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>目标跨度。</para></param>
    /// <returns>Bytes copied, or zero at the end of the payload.
    /// <para>复制的字节数；负载结束时为 0。</para></returns>
    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);
        var alignedStart = _position - (_position % CipherBlockSize);
        var alignedEnd = Math.Min(_storedLength, AlignUp(_position + count));
        var spanLength = checked((int)(alignedEnd - alignedStart));
        var cipher = ArrayPool<byte>.Shared.Rent(spanLength);
        try
        {
            var cipherSpan = cipher.AsSpan(0, spanLength);
            lock (_container)
            {
                _container.Position = _containerOffset + alignedStart;
                _container.ReadExactly(cipherSpan);
            }

            if (_decrypt)
            {
                AllwinnerImageWtyCrypto.DecryptBlocks(ImageWtyCipherScope.Content, cipherSpan);
            }

            var start = (int)(_position - alignedStart);
            cipherSpan.Slice(start, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipher);
        }
    }

    /// <summary>
    /// Reads decrypted logical bytes into a byte array.
    /// <para>把解密后的逻辑字节读入字节数组。</para>
    /// </summary>
    /// <param name="buffer">Destination array. <para>目标数组。</para></param>
    /// <param name="offset">First destination index. <para>首个目标索引。</para></param>
    /// <param name="count">Maximum bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>Bytes copied, or zero at the end of the payload.
    /// <para>复制的字节数；负载结束时为 0。</para></returns>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>Moves the logical position. <para>移动逻辑位置。</para></summary>
    /// <param name="offset">Signed offset. <para>带符号偏移。</para></param>
    /// <param name="origin">Reference point. <para>参考点。</para></param>
    /// <returns>The new position. <para>新位置。</para></returns>
    /// <exception cref="IOException">The resulting position is negative or beyond the payload.
    /// <para>结果位置为负或超出负载。</para></exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin.")
        };
        if (target < 0 || target > _length)
        {
            throw new IOException("The seek target is outside the entry payload.");
        }

        _position = target;
        return _position;
    }

    /// <summary>Not supported. <para>不支持。</para></summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <exception cref="NotSupportedException">Always thrown. <para>总是抛出。</para></exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Not supported. <para>不支持。</para></summary>
    /// <exception cref="NotSupportedException">Always thrown. <para>总是抛出。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static long AlignUp(long value) => (value + CipherBlockSize - 1) & ~(CipherBlockSize - 1L);
}