namespace UotanStudio.Compress.Codecs.Lz4;

/// <summary>
/// Decodes a frameless LZ4 raw block from a stream into a stream while keeping the last 64 KiB of output as the match
/// window.
/// <para>以流的方式解码无帧 LZ4 原始块并写入目标流，解码过程中保留最近 64 KiB 输出作为匹配窗口。</para>
/// </summary>
/// <remarks>
/// The block carries no frame header, no content size and no checksum: every token is <c>literalLengthHigh |
/// matchLengthLow</c>, a literal run, a two-byte little-endian match offset and an extended match length of at least
/// four bytes. The caller supplies the packed byte count and the exact expected output length, and both are verified
/// when the block ends, so a block that leaves input behind or underfills the declared size is rejected rather than
/// silently accepted.
/// The window is maintained byte by byte while output is still being produced, so an overlapping match that reads
/// the bytes it just wrote is reproduced exactly; the decoder never buffers the whole block.
/// Failures are reported through <see cref="Lz4RawBlockException"/> with a machine-readable reason instead of a
/// vendor-specific sentence, because the wording a caller shows its user belongs to that caller; the reason carries the
/// decoded and expected lengths so the caller can rebuild its own message.
/// <para>该块不含帧头、内容长度与校验和：每个标记为 <c>字面量长度高位 | 匹配长度低位</c>，其后是字面量段、2 字节小端序匹配偏移，
/// 以及最少 4 字节的扩展匹配长度。调用方给出压缩字节数与期望输出长度，块结束时两者都会被核对，因此遗留输入字节或输出不足的块会被拒绝而非静默接受。
/// 窗口在输出产生的同时逐字节维护，因此读取刚刚写出字节的自重叠匹配能被精确还原；解码器不会把整块缓存进内存。
/// 失败通过 <see cref="Lz4RawBlockException"/> 以可机读的原因上报，而不是带厂商色彩的文案，因为面向用户的措辞属于调用方；
/// 原因会附带已解码与期望长度，便于调用方重建自己的文案。</para>
/// </remarks>
internal static class Lz4RawStreamDecoder
{
    private const int HistorySize = 65_536;

    /// <summary>
    /// Decodes one LZ4 raw block from the current position of <paramref name="input"/> into <paramref
    /// name="output"/>.
    /// <para>把 <paramref name="input"/> 当前位置起的一个 LZ4 原始块解码并写入 <paramref name="output"/>。</para>
    /// </summary>
    /// <param name="input">Readable stream positioned at the first byte of the block; it is read synchronously and left open.
    /// <para>定位到块首字节的可读流；以同步方式读取且不会被关闭。</para></param>
    /// <param name="packedLength">Exact number of bytes the block occupies; the decoder never reads beyond it.
    /// <para>块占用的精确字节数；解码器不会读取超出该值的字节。</para></param>
    /// <param name="output">Writable stream receiving the decoded bytes; it is flushed by this call but not closed.
    /// <para>接收解码结果的可写流；本次调用会刷新但不会关闭它。</para></param>
    /// <param name="expectedLength">Exact number of bytes the block must produce; a shorter or longer result fails.
    /// <para>块必须产出的精确字节数；过短或过长的结果都会失败。</para></param>
    /// <param name="cancellationToken">Token checked before every token, literal run, match run and flush.
    /// <para>在每个标记、字面量段、匹配段与刷新前检查的取消令牌。</para></param>
    /// <returns>A completed task when the block decoded to exactly <paramref name="expectedLength"/> bytes and consumed
    /// <paramref name="packedLength"/> bytes.
    /// <para>当块恰好解码出 <paramref name="expectedLength"/> 字节并读满 <paramref name="packedLength"/> 字节时返回已完成的任务。</para></returns>
    /// <exception cref="Lz4RawBlockException">A literal run or match run exceeds the declared output size, a match offset is out of range, the block ends before its declared byte count, or the decoded length does not match. <para>字面量段或匹配段超出声明输出大小、匹配偏移越界、块在声明字节数之前结束，或解码长度与声明不符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static Task DecodeAsync(
        Stream input,
        long packedLength,
        Stream output,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var reader = new BoundedByteReader(input, packedLength);
        var writer = new HistoryWriter(output);
        while (reader.Remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = reader.ReadByte(cancellationToken);
            var literalLength = ReadLength(reader, token >> 4, cancellationToken);
            if (literalLength > expectedLength - writer.Written)
            {
                throw new Lz4RawBlockException(
                    Lz4RawBlockFailure.LiteralLengthOverflow,
                    "The LZ4 raw block literal run exceeds the declared output size.");
            }
            writer.CopyLiterals(reader, literalLength, cancellationToken);
            if (reader.Remaining == 0)
            {
                break;
            }

            var offsetLow = reader.ReadByte(cancellationToken);
            var offsetHigh = reader.ReadByte(cancellationToken);
            var offset = offsetLow | offsetHigh << 8;
            if (offset == 0 || offset > writer.Written || offset > HistorySize - 1)
            {
                throw new Lz4RawBlockException(
                    Lz4RawBlockFailure.MatchOffsetInvalid,
                    "The LZ4 raw block match offset is outside the decoded history window.");
            }
            var matchLength = checked(ReadLength(reader, token & 0x0F, cancellationToken) + 4);
            if (matchLength > expectedLength - writer.Written)
            {
                throw new Lz4RawBlockException(
                    Lz4RawBlockFailure.MatchLengthOverflow,
                    "The LZ4 raw block match run exceeds the declared output size.");
            }
            writer.CopyMatch(offset, matchLength, cancellationToken);
        }

        writer.Flush(cancellationToken);
        if (reader.Remaining != 0 || writer.Written != expectedLength)
        {
            throw new Lz4RawBlockException(
                Lz4RawBlockFailure.OutputLengthMismatch,
                $"The LZ4 raw block decoded {writer.Written} bytes; expected {expectedLength} bytes.",
                writer.Written,
                expectedLength);
        }

        return Task.CompletedTask;
    }

    private static long ReadLength(
        BoundedByteReader reader,
        int initial,
        CancellationToken cancellationToken)
    {
        var length = (long)initial;
        if (initial != 15)
        {
            return length;
        }
        byte extension;
        do
        {
            extension = reader.ReadByte(cancellationToken);
            length = checked(length + extension);
        }
        while (extension == byte.MaxValue);
        return length;
    }

    private sealed class BoundedByteReader(Stream input, long remaining)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _offset;
        private int _count;
        public long Remaining { get; private set; } = remaining;

        public int Buffered => _count - _offset;

        public ReadOnlySpan<byte> BufferedSpan => _buffer.AsSpan(_offset, _count - _offset);

        public byte ReadByte(CancellationToken cancellationToken)
        {
            if (Remaining == 0)
            {
                throw new Lz4RawBlockException(
                    Lz4RawBlockFailure.BlockTruncated,
                    "The LZ4 raw block ended before its declared byte count.");
            }
            if (_offset == _count)
            {
                Fill(cancellationToken);
            }
            Remaining--;
            return _buffer[_offset++];
        }

        public void Fill(CancellationToken cancellationToken)
        {
            if (Remaining == 0)
            {
                throw new Lz4RawBlockException(
                    Lz4RawBlockFailure.BlockTruncated,
                    "The LZ4 raw block ended before its declared byte count.");
            }
            _count = input.Read(_buffer, 0, (int)Math.Min(_buffer.Length, Remaining));
            _offset = 0;
            if (_count == 0)
            {
                throw new Lz4RawBlockException(
                    Lz4RawBlockFailure.SourceEndedEarly,
                    "The LZ4 raw block source stream ended before its declared byte count.");
            }
        }

        public void Advance(int count)
        {
            _offset += count;
            Remaining -= count;
        }
    }

    private sealed class HistoryWriter(Stream output)
    {
        private readonly byte[] _history = new byte[HistorySize];
        private readonly byte[] _outputBuffer = new byte[128 * 1024];
        private int _historyPosition;
        private int _bufferCount;
        public long Written { get; private set; }

        public void CopyLiterals(BoundedByteReader reader, long length, CancellationToken cancellationToken)
        {
            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Buffered == 0)
                {
                    reader.Fill(cancellationToken);
                }
                var count = (int)Math.Min(length, reader.Buffered);
                Write(reader.BufferedSpan[..count], cancellationToken);
                reader.Advance(count);
                length -= count;
            }
        }

        public void CopyMatch(int offset, long length, CancellationToken cancellationToken)
        {
            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = (_historyPosition - offset) & (HistorySize - 1);
                var run = Math.Min(Math.Min(HistorySize - source, HistorySize - _historyPosition), offset);
                var count = (int)Math.Min(length, Math.Min(run, _outputBuffer.Length - _bufferCount));
                _history.AsSpan(source, count).CopyTo(_outputBuffer.AsSpan(_bufferCount, count));
                _outputBuffer.AsSpan(_bufferCount, count).CopyTo(_history.AsSpan(_historyPosition, count));
                _historyPosition = (_historyPosition + count) & (HistorySize - 1);
                _bufferCount += count;
                Written += count;
                length -= count;
                if (_bufferCount == _outputBuffer.Length)
                {
                    Flush(cancellationToken);
                }
            }
        }

        public void Flush(CancellationToken cancellationToken)
        {
            if (_bufferCount > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(_outputBuffer, 0, _bufferCount);
                _bufferCount = 0;
            }
        }

        private void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
        {
            while (!data.IsEmpty)
            {
                var count = Math.Min(data.Length, _outputBuffer.Length - _bufferCount);
                data[..count].CopyTo(_outputBuffer.AsSpan(_bufferCount, count));
                AppendHistory(data[..count]);
                _bufferCount += count;
                Written += count;
                data = data[count..];
                if (_bufferCount == _outputBuffer.Length)
                {
                    Flush(cancellationToken);
                }
            }
        }

        private void AppendHistory(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                var count = Math.Min(data.Length, HistorySize - _historyPosition);
                data[..count].CopyTo(_history.AsSpan(_historyPosition, count));
                _historyPosition = (_historyPosition + count) & (HistorySize - 1);
                data = data[count..];
            }
        }
    }
}

/// <summary>
/// Reasons a frameless LZ4 raw block can be rejected.
/// <para>无帧 LZ4 原始块可能遭到拒绝的原因。</para>
/// </summary>
internal enum Lz4RawBlockFailure
{
    /// <summary>A literal run would produce more bytes than the declared output size.
    /// <para>字面量段会产出超过声明输出大小的字节。</para></summary>
    LiteralLengthOverflow,

    /// <summary>A match offset is zero, points before the decoded bytes, or leaves the 64 KiB window.
    /// <para>匹配偏移为 0、指向已解码字节之前，或超出 64 KiB 窗口。</para></summary>
    MatchOffsetInvalid,

    /// <summary>A match run would produce more bytes than the declared output size.
    /// <para>匹配段会产出超过声明输出大小的字节。</para></summary>
    MatchLengthOverflow,

    /// <summary>The block ended with input left over or with a decoded length other than the declared one.
    /// <para>块结束时仍有输入未消费，或解码长度与声明长度不一致。</para></summary>
    OutputLengthMismatch,

    /// <summary>The block asked for another byte although its declared byte count was already spent.
    /// <para>块的声明字节数已经用尽，却仍要求读取下一个字节。</para></summary>
    BlockTruncated,

    /// <summary>The source stream stopped short of the declared block size.
    /// <para>源流在声明块大小之前结束。</para></summary>
    SourceEndedEarly,
}

/// <summary>
/// Reports a malformed LZ4 raw block together with the machine-readable reason a caller needs to phrase the failure in
/// its own format vocabulary.
/// <para>报告格式错误的 LZ4 原始块，并附带调用方按自身格式措辞所需的可机读原因。</para>
/// </summary>
internal sealed class Lz4RawBlockException : Exception
{
    /// <summary>
    /// Creates the failure with its reason and neutral message.
    /// <para>以原因与中性文案创建该失败。</para>
    /// </summary>
    /// <param name="reason">Why the block was rejected. <para>块被拒绝的原因。</para></param>
    /// <param name="message">Format-neutral description of the failure. <para>与格式无关的失败描述。</para></param>
    /// <param name="decodedBytes">Bytes produced so far, when the failure reports a length.
    /// <para>当该失败涉及长度时，迄今已产出的字节数。</para></param>
    /// <param name="expectedBytes">Output length the caller declared, when the failure reports a length.
    /// <para>当该失败涉及长度时，调用方声明的输出长度。</para></param>
    public Lz4RawBlockException(Lz4RawBlockFailure reason, string message, long decodedBytes = 0, long expectedBytes = 0)
        : base(message)
    {
        Reason = reason;
        DecodedBytes = decodedBytes;
        ExpectedBytes = expectedBytes;
    }

    /// <summary>The reason the block was rejected.
    /// <para>块被拒绝的原因。</para></summary>
    /// <value>One of the <see cref="Lz4RawBlockFailure"/> members. <para><see cref="Lz4RawBlockFailure"/> 的成员之一。</para></value>
    public Lz4RawBlockFailure Reason { get; }

    /// <summary>The number of bytes the decoder had produced when the block was rejected.
    /// <para>块被拒绝时解码器已产出的字节数。</para></summary>
    /// <value>The decoded byte count for <see cref="Lz4RawBlockFailure.OutputLengthMismatch"/>; otherwise zero.
    /// <para>仅在 <see cref="Lz4RawBlockFailure.OutputLengthMismatch"/> 时为已解码字节数，其余情况为 0。</para></value>
    public long DecodedBytes { get; }

    /// <summary>The output length the caller declared for the block.
    /// <para>调用方为该块声明的输出长度。</para></summary>
    /// <value>The declared length for <see cref="Lz4RawBlockFailure.OutputLengthMismatch"/>; otherwise zero.
    /// <para>仅在 <see cref="Lz4RawBlockFailure.OutputLengthMismatch"/> 时为声明长度，其余情况为 0。</para></value>
    public long ExpectedBytes { get; }
}