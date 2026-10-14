using UotanStudio.Hash.Sums;

namespace UotanStudio.Firmware.Archive.Formats.Tar;

/// <summary>
/// Forward-only pass-through stream that validates every TAR header block's octal checksum while the
/// archive is being read, so a corrupted header is rejected on every target framework instead of only on
/// the runtimes whose <c>System.Formats.Tar</c> reader performs that check itself.
/// <para>只进先出的透传流，在读取归档期间校验每个 TAR 头块的八进制校验和，使损坏的头块在所有目标框架上
/// 都被拒绝，而不是仅在 <c>System.Formats.Tar</c> 自带该检查的运行时上才被拒绝。</para>
/// </summary>
/// <remarks>
/// A header block starts every 512-byte record chain: its own bytes, then
/// <c>ceil(Size / 512)</c> data records, then the next header. The checksum field at offset 148 is
/// treated as spaces when the sum is computed, exactly as the writer does. A zero-filled block ends
/// the archive and everything after it is passed through unchecked.
/// <para>头块位于 512 字节记录链的起点：头部本身、随后 <c>ceil(Size / 512)</c> 条数据记录、再接下一个头部。
/// 计算校验和时，偏移 148 的校验和字段按空格处理，与写入器一致。全零块表示归档结束，其后的字节不再校验。</para>
/// <para>A seek is reinterpreted against the record range already parsed: landing on the next header or
/// inside the current payload keeps validation aligned, while an offset outside both ranges stops it
/// because the record boundaries can no longer be derived.
/// 定位请求会按已解析的记录范围重新解释：落在下一个头块或当前负载范围内时校验保持对齐，
/// 超出两者范围的偏移会因无法再推断记录边界而停止校验。</para>
/// </remarks>
internal sealed class TarHeaderChecksumStream(Stream inner) : Stream
{
    private const int BlockSize = 512;
    private const int ChecksumOffset = 148;
    private const int SizeOffset = 124;
    private const int SizeLength = 12;

    private readonly byte[] _header = new byte[BlockSize];

    private long _position;
    private int _headerBytes;
    private long _remainingDataBytes;
    private long _headerStartAt;
    private long _nextHeaderAt;
    private bool _archiveEnded;
    private bool _trackingDisabled;

    /// <inheritdoc/>
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The inner stream does not expose a length.</exception>
    public override long Length => inner.Length;

    /// <inheritdoc/>
    /// <exception cref="IOException">A seek would move off a 512-byte header boundary.</exception>
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>
    /// Reads the next bytes of the archive, validating any header block completed by this call.
    /// <para>读取归档的后续字节，并校验本次调用完成的头块。</para>
    /// </summary>
    /// <param name="buffer">Destination for the raw archive bytes. <para>归档原始字节的接收缓冲区。</para></param>
    /// <param name="offset">Index in <paramref name="buffer"/> where reading starts.
    /// <para><paramref name="buffer"/> 中开始接收的索引。</para></param>
    /// <param name="count">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <returns>The number of bytes read by the inner stream. <para>底层流实际读取的字节数。</para></returns>
    /// <exception cref="InvalidDataException">A header block's checksum does not match its bytes.
    /// <para>某个头块的校验和与其字节内容不一致。</para></exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateRange(buffer, offset, count);
        if (count == 0)
        {
            return 0;
        }

        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            Observe(buffer.AsSpan(offset, read));
            _position += read;
        }

        return read;
    }

    /// <summary>
    /// Reads the next bytes asynchronously, with the same header validation as <see cref="Read"/>.
    /// <para>异步读取后续字节，校验规则与 <see cref="Read"/> 相同。</para>
    /// </summary>
    /// <param name="buffer">Destination for the raw archive bytes; at most its length is read.
    /// <para>归档原始字节的接收缓冲区；最多读取其长度个字节。</para></param>
    /// <param name="cancellationToken">Cancels the pending read. <para>取消尚未完成的读取。</para></param>
    /// <returns>A task producing the number of bytes read. <para>产出已读取字节数的任务。</para></returns>
    /// <exception cref="InvalidDataException">A header block's checksum does not match its bytes.
    /// <para>某个头块的校验和与其字节内容不一致。</para></exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            Observe(buffer.Span[..read]);
            _position += read;
        }

        return read;
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this stream never writes.</exception>
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this stream never writes.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this stream never writes.</exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = inner.Seek(offset, origin);
        _position = target;
        _headerBytes = 0;
        if (_trackingDisabled)
        {
            return target;
        }

        if (target == _nextHeaderAt || target == _headerStartAt)
        {
            _remainingDataBytes = 0;
        }
        else if (target >= _headerStartAt + BlockSize && target < _nextHeaderAt)
        {
            _remainingDataBytes = _nextHeaderAt - target;
        }
        else
        {
            _trackingDisabled = true;
            _remainingDataBytes = 0;
        }

        return target;
    }

    /// <summary>
    /// Feeds observed bytes through the header/data state machine.
    /// <para>把观察到的字节馈入头部/数据状态机。</para>
    /// </summary>
    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_trackingDisabled || _archiveEnded)
        {
            return;
        }

        var cursor = _position;
        while (!bytes.IsEmpty)
        {
            if (_remainingDataBytes > 0)
            {
                var skipped = (long)Math.Min(bytes.Length, _remainingDataBytes);
                _remainingDataBytes -= skipped;
                cursor += skipped;
                bytes = bytes[(int)skipped..];
                continue;
            }

            var take = Math.Min(bytes.Length, BlockSize - _headerBytes);
            bytes[..take].CopyTo(_header.AsSpan(_headerBytes, take));
            _headerBytes += take;
            cursor += take;
            bytes = bytes[take..];
            if (_headerBytes < BlockSize)
            {
                continue;
            }

            var headerStart = cursor - BlockSize;
            _headerBytes = 0;
            if (IsZeroBlock(_header))
            {
                _headerStartAt = headerStart;
                _nextHeaderAt = headerStart + BlockSize;
                _archiveEnded = true;
                return;
            }

            ValidateHeader(_header, headerStart);
            _headerStartAt = headerStart;
            _nextHeaderAt = headerStart + BlockSize + RoundUpToBlocks(ReadOctal(_header.AsSpan(SizeOffset, SizeLength)));
            _remainingDataBytes = _nextHeaderAt - cursor;
        }
    }

    /// <summary>
    /// Recomputes a header block's checksum and rejects the archive when the recorded value differs.
    /// <para>重新计算头块的校验和，记录值不一致时拒绝该归档。</para>
    /// </summary>
    private static void ValidateHeader(byte[] header, long headerStart)
    {
        var recorded = ReadOctal(header.AsSpan(ChecksumOffset, 8));
        Span<byte> block = stackalloc byte[BlockSize];
        header.AsSpan(0, BlockSize).CopyTo(block);
        block[ChecksumOffset..(ChecksumOffset + 8)].Fill((byte)' ');
        var computed = ByteSum32.Compute(block);

        if (computed != recorded)
        {
            throw new InvalidDataException(
                $"TAR header checksum is invalid at offset {headerStart}: the block records {recorded} but its bytes sum to {computed}.");
        }
    }

    private static long RoundUpToBlocks(long size) => size <= 0 ? 0 : (size + BlockSize - 1) / BlockSize * BlockSize;

    private static bool IsZeroBlock(byte[] block)
    {
        foreach (var value in block)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes a tar octal field, accepting both the space- or NUL-terminated classic form and the
    /// base-256 encoding that GNU writers use for values wider than the field.
    /// <para>解码 tar 八进制字段，同时接受以空格或 NUL 结尾的经典形式，以及 GNU 写入器用于超出字段宽度的
    /// base-256 编码。</para>
    /// </summary>
    private static long ReadOctal(ReadOnlySpan<byte> field)
    {
        if (field.Length > 0 && (field[0] & 0x80) != 0)
        {
            var negative = (field[0] & 0xFF) == 0xFF;
            long value = negative ? -1 : 0;
            for (var i = 1; i < field.Length; i++)
            {
                value = (value << 8) | field[i];
            }

            return value;
        }

        long result = 0;
        var started = false;
        foreach (var current in field)
        {
            if (current is (byte)' ' or 0)
            {
                if (started)
                {
                    break;
                }

                continue;
            }

            if (current < (byte)'0' || current > (byte)'7')
            {
                return -1;
            }

            result = (result << 3) + (current - '0');
            started = true;
        }

        return result;
    }

    private static void ValidateRange(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || buffer.Length - offset < count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The read range exceeds the buffer.");
        }
    }
}