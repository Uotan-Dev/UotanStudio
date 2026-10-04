using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Flash-mode entry, raw partition-table transfer, section programming, and stream close for the
/// streaming protocol, transcribed from <c>edlclient/Library/streaming.py</c> (enter_flash_mode
/// at streaming.py:177-187, send_ptable at streaming.py:243-257, send_section_header at
/// streaming.py:169-175, write_flash at streaming.py:189-215, qclose at streaming.py:160-167).
/// <para>流式协议的进入闪存模式、原始分区表传输、段编程与关闭流，转写自
/// <c>edlclient/Library/streaming.py</c>（enter_flash_mode 见 streaming.py:177-187，send_ptable
/// 见 streaming.py:243-257，send_section_header 见 streaming.py:169-175，write_flash 见
/// streaming.py:189-215，qclose 见 streaming.py:160-167）。</para>
/// </summary>
public sealed partial class StreamingProtocol
{
    /// <summary>
    /// Runs the secure-mode negotiation followed by the qclose exchange that puts the target into
    /// flash mode. Like the reference <c>enter_flash_mode</c>, the qclose outcome is logged but
    /// does not abort the entry; the returned value reports whether the target acknowledged the
    /// secure-mode negotiation (streaming.py:177-187).
    /// <para>执行安全模式协商，随后执行让目标进入闪存模式的 qclose 交换。与参考
    /// <c>enter_flash_mode</c> 相同，qclose 结果只记录而不中断进入；返回值报告目标是否确认了
    /// 安全模式协商（streaming.py:177-187）。</para>
    /// </summary>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the flash-mode transition.
    /// <para>传 <see langword="true"/> 以确认切换到闪存模式。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the target acknowledged secure mode.
    /// <para>目标确认安全模式时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is
    /// <see langword="false"/>; the refusal happens before the operation lease and before any
    /// byte reaches the wire. <para><paramref name="confirm"/> 为 <see langword="false"/> 时抛出；
    /// 拒绝发生在取得操作租约之前，任何字节都不落到线上。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<bool> EnterFlashModeAsync(bool confirm, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException(
                "Entering streaming flash mode requires explicit confirmation.", nameof(confirm));
        }

        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        Log("Entering streaming flash mode...");
        var secure = await SecureModeOwnedAsync(trusted: true, cancellationToken).ConfigureAwait(false);
        var closed = await CloseOwnedAsync(cancellationToken).ConfigureAwait(false);
        if (!closed)
        {
            Log("The stream close preceding flash mode did not confirm.");
        }

        return secure;
    }

    /// <summary>
    /// Sends the raw on-device partition table with DIAG command 0x19. The table length is bound
    /// to <see cref="StreamingConstants.PartitionTableBytes"/> before any byte is written, and
    /// the response status byte is interpreted with the reference meanings: 0 accepted, 1 differs
    /// but override accepted, 2 format unrecognized, 3 erase failed (streaming.py:243-257). The
    /// reference combines its failure test with <c>and</c> instead of <c>or</c>
    /// (streaming.py:247); this decoder requires the response identifier and reports acceptance
    /// for status 0 or 1.
    /// <para>用 DIAG 命令 0x19 发送原始设备分区表。表长在任一字节的写出之前已按
    /// <see cref="StreamingConstants.PartitionTableBytes"/> 限制，并按参考含义解析响应状态字节：
    /// 0 接受、1 不同但允许覆盖、2 格式无法识别、3 擦除失败（streaming.py:243-257）。参考把失败
    /// 判断写成了 <c>and</c> 而不是 <c>or</c>（streaming.py:247）；这里要求响应号匹配，并在状态为
    /// 0 或 1 时报告接受。</para>
    /// </summary>
    /// <param name="partitionTable">The raw partition table bytes, at most
    /// <see cref="StreamingConstants.PartitionTableBytes"/> long.
    /// <para>原始分区表字节，至多 <see cref="StreamingConstants.PartitionTableBytes"/> 字节。</para></param>
    /// <param name="overrideExisting">Pass <see langword="true"/> to send mode byte 0x01, which
    /// lets the target override its existing table. <para>传 <see langword="true"/> 发送模式字节
    /// 0x01，允许目标覆盖既有分区表。</para></param>
    /// <param name="confirmWrite">Pass <see langword="true"/> to confirm this destructive
    /// partition-table write. <para>传 <see langword="true"/> 以确认这次具有破坏性的分区表写入。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the target accepted the table (status 0 or 1).
    /// <para>目标接受该表（状态 0 或 1）时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the table is empty or longer
    /// than <see cref="StreamingConstants.PartitionTableBytes"/>; the refusal happens before the
    /// operation lease. <para>表为空或超过 <see cref="StreamingConstants.PartitionTableBytes"/>
    /// 时抛出；拒绝发生在取得操作租约之前。</para></exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirmWrite"/> is
    /// <see langword="false"/>. <para><paramref name="confirmWrite"/> 为
    /// <see langword="false"/> 时抛出。</para></exception>
    /// <exception cref="StreamingException">Thrown when the target answers with a wrong
    /// partition-table response identifier. <para>目标以错误的分区表响应号应答时抛出。</para></exception>
    public async Task<bool> SendPartitionTableAsync(ReadOnlyMemory<byte> partitionTable,
        bool overrideExisting, bool confirmWrite, CancellationToken cancellationToken = default)
    {
        if (partitionTable.Length is 0 or > StreamingConstants.PartitionTableBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionTable),
                $"The streaming partition table must be between 1 and {StreamingConstants.PartitionTableBytes} bytes.");
        }

        if (!confirmWrite)
        {
            throw new ArgumentException(
                "Writing the streaming partition table requires an explicit confirmation.", nameof(confirmWrite));
        }

        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        var payload = new byte[2 + partitionTable.Length];
        payload[0] = StreamingConstants.DiagFlashModeTableCommand;
        payload[1] = overrideExisting ? (byte)0x01 : (byte)0x00;
        partitionTable.Span.CopyTo(payload.AsSpan(2));

        var reply = await SendDiagOwnedAsync(payload, verifyResponseCrc: true,
            tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
        if (reply.Length < 2 || reply[0] != StreamingConstants.DiagFlashModeTableResponse)
        {
            throw new StreamingException(EdlErrorCode.TargetNak,
                "The streaming target rejected the raw partition table command.");
        }

        var status = reply[1];
        Log(status switch
        {
            0x00 => "Partition table accepted.",
            0x01 => "Partition table differs; override accepted.",
            0x02 => "Partition table format not recognized; override refused.",
            0x03 => "Partition table erase failed.",
            _ => $"Partition table responded with unknown status 0x{status:X2}."
        });
        return status is 0x00 or 0x01;
    }

    /// <summary>
    /// Programs one partition section: the DIAG 0x1B/0x0E section header naming the partition,
    /// then ceil(<paramref name="length"/>, 1024) program frames, each 0x07 with a little-endian
    /// byte address and a 1024-byte chunk whose tail is padded with 0xFF, every chunk requiring
    /// the 0x08 acknowledgement, and a final qclose (streaming.py:169-215). The address window is
    /// validated with checked 64-bit arithmetic against the 32-bit DIAG wire address space before
    /// the operation lease or any transport write, and the source is consumed chunk by chunk with
    /// no whole-image buffering.
    /// <para>编程一个分区段：先以 DIAG 0x1B/0x0E 段头指明分区，随后发送
    /// ceil(<paramref name="length"/>, 1024) 个编程帧，每帧为 0x07 加小端字节地址与 1024 字节块，
    /// 块尾以 0xFF 补齐，每块都要求 0x08 确认，最后 qclose（streaming.py:169-215）。地址窗口在
    /// 取得操作租约与任何传输写出之前，用 checked 64 位运算对照 32 位 DIAG 线路地址空间校验；
    /// 数据源逐块消费，绝不整镜像缓冲。</para>
    /// </summary>
    /// <param name="lba">The byte-address window start the target's program command expects;
    /// the reference passes the partition start (streaming.py:189, 197-208).
    /// <para>目标的编程命令所使用的字节地址窗口起点；参考实现传入分区起始地址
    /// （streaming.py:189、197-208）。</para></param>
    /// <param name="partition">The partition section name sent inside the section header.
    /// <para>段头中发送的分区段名。</para></param>
    /// <param name="source">The image source stream, read chunk by chunk.
    /// <para>镜像源流，逐块读取。</para></param>
    /// <param name="length">The number of source bytes to program.
    /// <para>要编程的源字节数。</para></param>
    /// <param name="progress">Optional progress sink reporting cumulative programmed source bytes.
    /// <para>可选的进度回调，报告累计编程的源字节数。</para></param>
    /// <param name="confirmWrite">Pass <see langword="true"/> to confirm the flash write.
    /// <para>传 <see langword="true"/> 以确认这次闪存写入。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the final qclose was acknowledged.
    /// <para>最后的 qclose 得到确认时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is
    /// negative or the program window leaves the 32-bit DIAG address space; the refusal happens
    /// before the operation lease and no byte is written. <para><paramref name="length"/> 为负，
    /// 或编程窗口超出 32 位 DIAG 地址空间时抛出；拒绝发生在取得操作租约之前，不写出任何字节。</para></exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirmWrite"/> is
    /// <see langword="false"/>, the partition name is empty or oversized, or the stream is not
    /// readable. <para><paramref name="confirmWrite"/> 为 <see langword="false"/>、段名为空或过长、
    /// 或流不可读时抛出。</para></exception>
    /// <exception cref="StreamingException">Thrown when the section header or a program chunk is
    /// not acknowledged, or when the source ends before <paramref name="length"/> bytes.
    /// <para>段头或编程块未被确认，或数据源在写满 <paramref name="length"/> 字节前提前结束时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<bool> WriteFlashAsync(uint lba, string partition, Stream source, long length,
        IProgress<long>? progress, bool confirmWrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!confirmWrite)
        {
            throw new ArgumentException(
                "Writing flash through the streaming protocol requires an explicit confirmation.", nameof(confirmWrite));
        }

        if (string.IsNullOrEmpty(partition))
        {
            throw new ArgumentException("The partition section name must not be empty.", nameof(partition));
        }

        if (!source.CanRead)
        {
            throw new ArgumentException("The flash image source stream must be readable.", nameof(source));
        }

        var nameBytes = Encoding.UTF8.GetByteCount(partition);
        if (nameBytes > StreamingConstants.MaxPacketBytes - 8)
        {
            throw new ArgumentException("The partition section name exceeds the DIAG packet budget.", nameof(partition));
        }

        var chunkCount = (length + StreamingConstants.WriteChunkBytes - 1) / StreamingConstants.WriteChunkBytes;
        var windowEnd = checked((long)lba + (chunkCount * StreamingConstants.WriteChunkBytes));
        if (windowEnd > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                "The program window leaves the 32-bit DIAG address space.");
        }

        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();

        var header = new byte[2 + SectionNamePrefix.Length + nameBytes + 1];
        header[0] = StreamingConstants.DiagSectionHeaderCommand;
        header[1] = StreamingConstants.DiagMultiImageUserDefined;
        Encoding.UTF8.GetBytes(SectionNamePrefix, header.AsSpan(2));
        Encoding.UTF8.GetBytes(partition, header.AsSpan(2 + SectionNamePrefix.Length));
        header[^1] = 0x00;
        var headerReply = await SendDiagOwnedAsync(header, verifyResponseCrc: true,
            tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
        if (headerReply.Length == 0 || headerReply[0] != StreamingConstants.DiagSectionHeaderResponse)
        {
            throw new StreamingException(EdlErrorCode.TargetNak,
                $"The streaming target rejected the section header for {partition}.");
        }

        var command = new byte[5 + StreamingConstants.WriteChunkBytes];
        command[0] = StreamingConstants.DiagProgramCommand;
        var chunk = command.AsMemory(5);
        var remaining = length;
        var programmed = 0L;
        var address = lba;
        while (remaining > 0)
        {
            var expected = (int)Math.Min(StreamingConstants.WriteChunkBytes, remaining);
            chunk.Span.Fill(0xFF);
            var filled = 0;
            while (filled < expected)
            {
                var read = await source.ReadAsync(chunk[filled..expected], cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new StreamingException(EdlErrorCode.FileIO,
                        $"The flash image source ended after {programmed} of {length} declared bytes.");
                }

                filled += read;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(1), address);
            var reply = await SendDiagOwnedAsync(command, verifyResponseCrc: true,
                tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
            if (reply.Length == 0 || reply[0] != StreamingConstants.DiagProgramResponse)
            {
                throw new StreamingException(EdlErrorCode.TargetNak,
                    $"The streaming target did not acknowledge the program chunk at address {address:X8}.");
            }

            address = checked(address + (uint)StreamingConstants.WriteChunkBytes);
            remaining -= expected;
            programmed += expected;
            progress?.Report(programmed);
        }

        Log($"Streaming write completed for {partition}: {programmed} bytes.");
        return await CloseOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finalizes the current stream section with qclose, mirroring the closing call the reference
    /// <c>write_flash</c> performs after the last chunk (streaming.py:211-215).
    /// <para>用 qclose 结束当前流式段，对应参考 <c>write_flash</c> 在最后一块之后的收尾调用
    /// （streaming.py:211-215）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the target answered with the close response
    /// identifier. <para>目标以关闭响应号应答时为 <see langword="true"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<bool> StreamCloseAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await CloseOwnedAsync(cancellationToken).ConfigureAwait(false);
    }
}