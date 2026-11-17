using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Reads up to a requested number of bytes from the RCM bulk endpoint into a caller-provided buffer.
/// <para>从 RCM 批量端点读取最多指定字节数到调用方提供的缓冲区。</para>
/// </summary>
/// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
/// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
/// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
/// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
/// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
/// <returns>The number of bytes read; zero means the transfer ended without data.
/// <para>实际读取的字节数；零表示传输结束且无数据。</para></returns>
public delegate ValueTask<int> TegraRcmReadAsync(
    byte[] buffer,
    int offset,
    int length,
    int timeoutMs,
    CancellationToken cancellationToken);

/// <summary>
/// Pure framing helpers for the RCM bulk protocol: how a host message is padded, how long a frame is, and how a
/// device reply is framed.
/// <para>RCM 批量协议的纯分帧辅助方法：主机消息如何填充、帧有多长，以及设备应答如何分帧。</para>
/// </summary>
/// <remarks>
/// This type performs no I/O. It only computes lengths and shapes bytes, so it stays usable in tests and in a
/// NativeAOT build without a device; <see cref="ReadResponseAsync"/> takes the read primitive as a delegate
/// instead of opening a transport itself. The transport-facing layer lives in the session type.
/// <para>本类型不做任何 I/O，只计算长度与拼装字节，因此在测试与 NativeAOT 构建中无需设备即可使用；
/// <see cref="ReadResponseAsync"/> 以委托形式接收读取原语，而不是自行打开传输。面向传输的层次位于会话类型中。</para>
/// </remarks>
public static class TegraRcmProtocol
{
    /// <summary>
    /// Largest frame this layer accepts on either direction: the maximum payload plus the largest header plus one
    /// minimum-message-worth of padding.
    /// <para>本层在任一方向上接受的最大帧：最大负载加最大头部，再预留一个最小消息长度的填充。</para>
    /// </summary>
    public const int MaximumFrameLength =
        TegraRcmMessage.MaximumPayloadLength + 0x284 + TegraConstants.RcmMinimumMessageLength;

    /// <summary>
    /// Smallest length a device reply may have: a bare 32-bit status word.
    /// <para>设备应答可能具有的最小长度：一个裸的 32 位状态字。</para>
    /// </summary>
    public const int MinimumReplyLength = 4;

    /// <summary>
    /// Largest reply this layer accepts: the 16-byte error record, or an error record plus its bounded text.
    /// <para>本层接受的最大应答：16 字节错误记录，或错误记录加上其有界文本。</para>
    /// </summary>
    public const int MaximumReplyLength = 16 + MaximumErrorMessageLength;

    /// <summary>
    /// Upper bound on the error text a device may attach to a 16-byte error reply, so a crafted length cannot make
    /// the host read without limit.
    /// <para>设备可在 16 字节错误应答后附带的错误文本上限，使伪造长度无法让主机无界读取。</para>
    /// </summary>
    public const int MaximumErrorMessageLength = 4096;

    /// <summary>
    /// Computes the padding that follows a host message's payload (<c>rcm_get_pad_len</c>).
    /// <para>计算主机消息负载之后的填充长度（<c>rcm_get_pad_len</c>）。</para>
    /// </summary>
    /// <param name="headerLength">The layout's header length. <para>布局的头部长度。</para></param>
    /// <param name="payloadLength">The payload length. <para>负载长度。</para></param>
    /// <returns>
    /// First the bytes needed to reach <see cref="TegraConstants.RcmMinimumMessageLength"/>, then 1 to 16 bytes that
    /// round the payload region (everything after the header) up to a whole AES block. The result is therefore
    /// always at least 1: the reference never leaves the payload region unpadded, which keeps the trailing
    /// <see cref="TegraConstants.PaddingFillByte"/> marker in place even for an already aligned message.
    /// <para>先补足到 <see cref="TegraConstants.RcmMinimumMessageLength"/> 所需的字节，再用 1 到 16 字节把负载区
    /// （头部之后的全部内容）对齐到整块 AES。因此结果至少为 1：参考实现对负载区始终填充，即使消息本就对齐也保
    /// 留末尾的 <see cref="TegraConstants.PaddingFillByte"/> 标记。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="headerLength"/> or
    /// <paramref name="payloadLength"/> is negative, or the payload exceeds
    /// <see cref="TegraRcmMessage.MaximumPayloadLength"/>. <para><paramref name="headerLength"/> 或
    /// <paramref name="payloadLength"/> 为负，或负载超过 <see cref="TegraRcmMessage.MaximumPayloadLength"/>。</para></exception>
    public static int GetPayloadPaddingLength(int headerLength, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(headerLength);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, TegraRcmMessage.MaximumPayloadLength);

        var messageLength = checked(headerLength + payloadLength);
        var paddingLength = 0;
        if (messageLength < TegraConstants.RcmMinimumMessageLength)
        {
            paddingLength = TegraConstants.RcmMinimumMessageLength - messageLength;
            messageLength += paddingLength;
        }

        paddingLength += 16 - ((messageLength - headerLength) & 0xf);
        return paddingLength;
    }

    /// <summary>
    /// Computes the total wire length of a host message (<c>len_insecure</c>).
    /// <para>计算主机消息的线上总长度（<c>len_insecure</c>）。</para>
    /// </summary>
    /// <param name="headerLength">The layout's header length. <para>布局的头部长度。</para></param>
    /// <param name="payloadLength">The payload length. <para>负载长度。</para></param>
    /// <returns>Header, payload and padding, rounded up to at least the minimum message length.
    /// <para>头部、负载与填充之和，向上补齐到至少最小消息长度。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="headerLength"/> or
    /// <paramref name="payloadLength"/> is out of range. <para><paramref name="headerLength"/> 或
    /// <paramref name="payloadLength"/> 超出范围。</para></exception>
    public static int GetFrameLength(int headerLength, int payloadLength)
        => checked(headerLength + payloadLength + GetPayloadPaddingLength(headerLength, payloadLength));

    /// <summary>
    /// Computes the total wire length of a host message for a specific chip generation.
    /// <para>计算某芯片代际主机消息的线上总长度。</para>
    /// </summary>
    /// <param name="chip">The target chip generation. <para>目标芯片代际。</para></param>
    /// <param name="payloadLength">The payload length. <para>负载长度。</para></param>
    /// <returns>The frame length in bytes. <para>帧长度（字节）。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is
    /// <see cref="TegraChip.Unknown"/>, or <paramref name="payloadLength"/> is out of range.
    /// <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>，或 <paramref name="payloadLength"/> 超出范围。</para></exception>
    public static int GetFrameLength(TegraChip chip, int payloadLength)
        => GetFrameLength(TegraRcmHeaderLayout.For(chip).HeaderLength, payloadLength);

    /// <summary>
    /// Builds a complete unsigned host frame: header, payload and padding, ready to be written in one bulk
    /// transfer.
    /// <para>构建完整的未签名主机帧：头部、负载与填充，可直接在一次批量传输中写出。</para>
    /// </summary>
    /// <param name="chip">The target chip generation. <para>目标芯片代际。</para></param>
    /// <param name="opcode">The recovery mode command. <para>恢复模式命令。</para></param>
    /// <param name="args">The command arguments, at most 48 bytes. <para>命令参数，最多 48 字节。</para></param>
    /// <param name="payload">The payload, at most <see cref="TegraRcmMessage.MaximumPayloadLength"/> bytes.
    /// <para>负载，最多 <see cref="TegraRcmMessage.MaximumPayloadLength"/> 字节。</para></param>
    /// <returns>The frame bytes. <para>帧字节。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="args"/> or <paramref name="payload"/> is too long.
    /// <para><paramref name="args"/> 或 <paramref name="payload"/> 过长。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is <see cref="TegraChip.Unknown"/>.
    /// <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>。</para></exception>
    /// <example>
    /// <code>
    /// var frame = TegraRcmProtocol.CreateFrame(TegraChip.Tegra114, TegraRcmOpcode.QueryRcmVersion);
    /// </code>
    /// </example>
    public static byte[] CreateFrame(
        TegraChip chip,
        TegraRcmOpcode opcode,
        ReadOnlySpan<byte> args = default,
        ReadOnlySpan<byte> payload = default)
        => TegraRcmMessage.Create(chip, opcode, args, payload).ToBytes();

    /// <summary>
    /// Builds a complete unsigned host frame asynchronously, for callers on an async path.
    /// <para>异步构建完整的未签名主机帧，供异步调用链使用。</para>
    /// </summary>
    /// <param name="chip">The target chip generation. <para>目标芯片代际。</para></param>
    /// <param name="opcode">The recovery mode command. <para>恢复模式命令。</para></param>
    /// <param name="args">The command arguments, at most 48 bytes. <para>命令参数，最多 48 字节。</para></param>
    /// <param name="payload">The payload, at most <see cref="TegraRcmMessage.MaximumPayloadLength"/> bytes.
    /// <para>负载，最多 <see cref="TegraRcmMessage.MaximumPayloadLength"/> 字节。</para></param>
    /// <param name="cancellationToken">A cancellation token; an already cancelled token prevents any work.
    /// <para>取消令牌；已取消的令牌会阻止任何工作。</para></param>
    /// <returns>A task that resolves to the frame bytes. <para>解析为帧字节的任务。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.
    /// <para><paramref name="cancellationToken"/> 已取消。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="args"/> or <paramref name="payload"/> is too long.
    /// <para><paramref name="args"/> 或 <paramref name="payload"/> 过长。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is <see cref="TegraChip.Unknown"/>.
    /// <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>。</para></exception>
    public static Task<byte[]> CreateAsync(
        TegraChip chip,
        TegraRcmOpcode opcode,
        ReadOnlyMemory<byte> args,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateFrame(chip, opcode, args.Span, payload.Span));
    }

    /// <summary>
    /// Validates the leading bytes of a host frame and reports the total length the header declares.
    /// <para>校验主机帧的首部字节并报告头部声明的总长度。</para>
    /// </summary>
    /// <param name="prefix">At least the first four bytes of the frame. <para>帧的前至少四个字节。</para></param>
    /// <param name="frameLength">Receives <c>len_insecure</c>. <para>接收 <c>len_insecure</c>。</para></param>
    /// <returns><see langword="true"/> when the declared length is representable and inside
    /// <see cref="MaximumFrameLength"/>. <para>声明的长度可表示且不超过 <see cref="MaximumFrameLength"/> 时为
    /// <see langword="true"/>。</para></returns>
    public static bool TryGetFrameLength(ReadOnlySpan<byte> prefix, out int frameLength)
    {
        frameLength = 0;
        if (prefix.Length < sizeof(uint))
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (declared < TegraRcmHeaderLayout.Rcm1.HeaderLength || declared > MaximumFrameLength)
        {
            return false;
        }

        frameLength = (int)declared;
        return true;
    }

    /// <summary>
    /// Reads one device reply after a host message has been written.
    /// <para>在主机消息写出之后读取一条设备应答。</para>
    /// </summary>
    /// <param name="read">The read primitive the caller supplies; this helper never opens a transport itself.
    /// <para>调用方提供的读取原语；本辅助方法从不自行打开传输。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds for each read. <para>每次读取的超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// The parsed reply. A 4-byte reply is a bare status or version word; a 16-byte reply is an error record whose
    /// text is read separately and bounded by <see cref="MaximumErrorMessageLength"/>.
    /// <para>解析出的应答。4 字节应答是裸状态字或版本字；16 字节应答是错误记录，其文本另行读取并受
    /// <see cref="MaximumErrorMessageLength"/> 限制。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.
    /// <para><paramref name="read"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The device answered fewer than four bytes, or a length neither four
    /// nor sixteen. <para>设备应答少于四字节，或为既非四也非十六的长度。</para></exception>
    /// <exception cref="IOException">The read primitive reported zero bytes. <para>读取原语返回零字节。</para></exception>
    public static async Task<TegraRcmReply> ReadResponseAsync(
        TegraRcmReadAsync read,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        var buffer = new byte[16];
        var total = 0;

        // The device answers with a short packet, so one read normally completes the reply; looping keeps short
        // transfers from being mistaken for a protocol error without ever exceeding the 16-byte reply record.
        // 设备以短包应答，通常一次读取即可完成；循环可避免把短传输误判为协议错误，同时不超过 16 字节应答记录。
        while (total < buffer.Length)
        {
            var readCount = await read(buffer, total, buffer.Length - total, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (readCount < 0 || readCount > buffer.Length - total)
            {
                throw new InvalidDataException(
                    $"The RCM read primitive reported {readCount} bytes for a {buffer.Length - total}-byte request.");
            }

            if (readCount == 0)
            {
                if (total == 0)
                {
                    throw new IOException("The device returned no RCM reply bytes within the timeout.");
                }

                break;
            }

            total += readCount;
        }

        if (total < MinimumReplyLength)
        {
            throw new InvalidDataException(
                $"An RCM reply is at least {MinimumReplyLength} bytes, but {total} were received.");
        }

        var reply = TegraRcmReply.Parse(buffer.AsSpan(0, total >= 16 ? 16 : total));

        // A 16-byte reply announces how much error text follows. Reading it here keeps every caller from
        // re-deriving the bound, and the length is clamped so a hostile device cannot extend the read.
        // 16 字节应答会声明后续错误文本的长度。在此读取可避免每个调用方各自推导上界，并对长度做钳制，使恶意设备
        // 无法延长读取。
        if (reply.MessageLength is > 0 and var messageLength)
        {
            var textLength = Math.Min((int)messageLength, MaximumErrorMessageLength);
            var text = new byte[textLength];
            var received = 0;
            while (received < textLength)
            {
                var readCount = await read(text, received, textLength - received, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (readCount <= 0)
                {
                    break;
                }

                received += readCount;
            }
        }

        return reply;
    }

    /// <summary>
    /// Reports whether the trailing bytes of a received frame carry the reference padding marker: a
    /// <see cref="TegraConstants.PaddingFillByte"/> followed only by zeros.
    /// <para>报告收到的帧尾字节是否带有参考实现的填充标记：一个
    /// <see cref="TegraConstants.PaddingFillByte"/> 其后全为零。</para>
    /// </summary>
    /// <param name="padding">The padding bytes to check. <para>待检查的填充字节。</para></param>
    /// <returns><see langword="true"/> when the run is empty or well formed.
    /// <para>该区段为空或格式正确时为 <see langword="true"/>。</para></returns>
    public static bool IsPaddingWellFormed(ReadOnlySpan<byte> padding)
    {
        if (padding.IsEmpty)
        {
            return true;
        }

        if (padding[0] != TegraConstants.PaddingFillByte)
        {
            return false;
        }

        for (var index = 1; index < padding.Length; index++)
        {
            if (padding[index] != 0)
            {
                return false;
            }
        }

        return true;
    }
}