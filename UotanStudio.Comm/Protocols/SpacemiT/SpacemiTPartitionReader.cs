using System.Globalization;
using UotanStudio.Comm.Protocols.Fastboot;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Reads partition content back from a SpacemiT U-Boot fastboot gadget with the device's
/// <c>oem read:&lt;partition&gt; [offset]</c> + <c>upload</c> protocol: each round stages at most
/// <c>min(partition remaining, CONFIG_FASTBOOT_BUF_SIZE)</c> bytes and answers OKAY with the staged
/// byte count as a hexadecimal string (with or without a <c>0x</c> prefix), after which the host
/// pulls the payload with a plain <c>upload</c> exchange.
/// <para>用设备端 <c>oem read:&lt;分区&gt; [offset]</c> + <c>upload</c> 协议从 SpacemiT U-Boot
/// fastboot gadget 回读分区内容：每轮设备暂存至多
/// <c>min(分区剩余, CONFIG_FASTBOOT_BUF_SIZE)</c> 字节，并以十六进制字符串（带或不带 <c>0x</c>
/// 前缀）的暂存字节数应答 OKAY，随后宿主以普通 <c>upload</c> 交换取回负载。</para>
/// </summary>
/// <remarks>
/// Before the first round the partition is probed through <c>getvar partition-size</c>: the gadget
/// answers a partition name missing from its GPT by falling back to the whole storage instead of
/// failing, so a partition the device does not report is refused and an open-ended read is bounded
/// at the device-reported size.
/// <para>首轮之前经 <c>getvar partition-size</c> 探测分区：gadget 对其 GPT 中不存在的分区名的应答是
/// 回退读整盘而不是报错，因此设备未上报的分区会被拒绝，未限定的回读也会按设备上报的大小限定。</para>
/// The device answers <c>OKAY00000000</c> (staged size 0) once the read offset reaches the
/// partition end, which terminates the loop. Offsets are sent as <c>0x&lt;hex&gt;</c> because the
/// device parses the argument with <c>simple_strtoul(..., 0)</c>; the host always advances on the
/// offsets it receives back so a device that floors unaligned offsets stays consistent.
/// <para>读偏移到达分区末尾时设备应答 <c>OKAY00000000</c>（暂存 0 字节），循环就此结束。偏移以
/// <c>0x&lt;十六进制&gt;</c> 发送，因为设备端以 <c>simple_strtoul(..., 0)</c> 解析该参数；宿主始终按
/// 收到的暂存长度推进偏移，因此把非对齐偏移向下取整的设备也保持一致。</para>
/// </remarks>
public static class SpacemiTPartitionReader
{
    /// <summary>Upper bound on the partition name length accepted in an <c>oem read</c> command. <para><c>oem read</c> 命令接受的分区名长度上限。</para></summary>
    private const int MaxPartitionNameLength = 64;

    /// <summary>Round count bound used when no byte limit is given, so a device that keeps staging non-empty buffers cannot loop forever. <para>未给出字节上限时的轮数上限，防止设备不断暂存非空缓冲导致死循环。</para></summary>
    private const int MaxReadRounds = 4096;

    /// <summary>Total byte bound used when no byte limit is given, capping a runaway read at 64 GiB. <para>未给出字节上限时的总字节上限，把失控回读限制在 64 GiB。</para></summary>
    private const long MaxTotalReadBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>Upper bound on the bytes a device may stage in one round: the vendor gadget caps each stage at CONFIG_FASTBOOT_BUF_SIZE (256 MiB) and MTD NOR rounds the read length up to four bytes, so a larger answer is malformed and would otherwise overflow the running offset. <para>设备单轮暂存字节的上限：厂商 gadget 把每轮暂存限制为 CONFIG_FASTBOOT_BUF_SIZE（256 MiB），MTD NOR 把读长向上取整到四字节，更大的应答即为异常，否则会使运行偏移溢出。</para></summary>
    private const long MaxStagedBytesPerRound = 256L * 1024 * 1024 + 4;

    /// <summary>
    /// Reads partition content back from the device into <paramref name="output"/>, looping
    /// <c>oem read</c>/<c>upload</c> rounds until the device stages nothing more or the byte limit
    /// is reached, and returns the total bytes received.
    /// <para>把分区内容从设备回读进 <paramref name="output"/>，循环 <c>oem read</c>/<c>upload</c>
    /// 轮次直到设备不再暂存内容或达到字节上限，并返回接收的总字节数。</para>
    /// </summary>
    /// <param name="session">Connected fastboot session for the U-Boot gadget. <para>连接 U-Boot gadget 的 fastboot 会话。</para></param>
    /// <param name="partition">Partition name; must be non-empty, at most 64 characters and free of whitespace or control characters. <para>分区名；不得为空、不超过 64 字符，且不含空白或控制字符。</para></param>
    /// <param name="output">Stream receiving the raw partition bytes at its current position; the caller retains ownership. <para>在当前位置接收分区原始字节的流；所有权由调用方保留。</para></param>
    /// <param name="maximumBytes">Upper bound on the bytes to read, or <see langword="null"/> to bound the read at the device-reported partition size after the presence probe. <para>读取字节上限；为 <see langword="null"/> 时在存在性探测后按设备上报的分区大小限定。</para></param>
    /// <param name="progress">Receives the cumulative byte count after each staged payload; may be <see langword="null"/>. <para>每轮暂存负载后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels between rounds, during the <c>oem read</c> and <c>upload</c> response waits and during payload transfer. <para>在轮次之间、<c>oem read</c> 与 <c>upload</c> 的应答等待期间以及负载传输期间取消。</para></param>
    /// <returns>The cumulative bytes written to <paramref name="output"/>, never above <paramref name="maximumBytes"/>. <para>写入 <paramref name="output"/> 的累计字节数，绝不超过 <paramref name="maximumBytes"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="partition"/> is empty, longer than 64 characters, or contains whitespace or control characters. <para><paramref name="partition"/> 为空、超过 64 字符，或含空白或控制字符。</para></exception>
    /// <exception cref="FastbootException">The device does not report the partition through <c>getvar partition-size</c>, an <c>oem read</c> or <c>upload</c> round failed, or the unbounded read exceeded the built-in safety caps. <para>设备未经 <c>getvar partition-size</c> 上报该分区、某轮 <c>oem read</c> 或 <c>upload</c> 失败，或不限长度的回读超出内置安全上限。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size. <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static long Read(
        FastbootSession session,
        string partition,
        Stream output,
        long? maximumBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidatePartitionName(partition);
        ArgumentNullException.ThrowIfNull(output);

        var probe = session.ExecuteCommand(
            FastbootConstants.CommandGetVar + FastbootConstants.VarPartitionSizePrefix + partition,
            cancellationToken);
        var deviceSize = probe.State == FastbootResponseState.Success
            ? FastbootSession.ParseDeviceSize(probe.Info)
            : 0;
        if (deviceSize <= 0)
        {
            var reason = probe.State switch
            {
                FastbootResponseState.Fail => probe.Info,
                FastbootResponseState.Timeout => "response timeout",
                _ => $"unexpected answer {probe.State}",
            };
            throw new FastbootException(FastbootResponseState.Fail,
                $"oem read {partition} was refused: the device did not report the partition ({reason}); " +
                "the gadget serves unknown partition names by reading the whole storage instead of failing, " +
                "so the read-back is not attempted");
        }

        maximumBytes ??= deviceSize;

        var offset = 0L;
        var totalRead = 0L;
        var rounds = 0;
        while (maximumBytes is null || totalRead < maximumBytes.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumBytes is null)
            {
                if (++rounds > MaxReadRounds)
                {
                    throw new FastbootException(FastbootResponseState.Fail,
                        $"oem read {partition} exceeded {MaxReadRounds} rounds; the device keeps staging data at offset 0x{offset:x}");
                }

                if (totalRead > MaxTotalReadBytes)
                {
                    throw new FastbootException(FastbootResponseState.Fail,
                        $"oem read {partition} exceeded the {MaxTotalReadBytes}-byte safety cap");
                }
            }

            var command = offset == 0
                ? $"oem read:{partition}"
                : $"oem read:{partition} 0x{offset:x}";
            var response = session.ExecuteCommand(command, cancellationToken);
            if (response.State != FastbootResponseState.Success)
            {
                throw new FastbootException(response.State,
                    $"oem read {partition} failed at offset 0x{offset:x}: {response.Text}");
            }

            var stagedBytes = ParseStagedSize(response.Info);
            if (stagedBytes <= 0)
            {
                break;
            }

            if (stagedBytes > MaxStagedBytesPerRound)
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"oem read {partition} staged {stagedBytes} bytes at offset 0x{offset:x}, above the {MaxStagedBytesPerRound}-byte per-round bound");
            }

            var allowed = maximumBytes is null ? long.MaxValue : maximumBytes.Value - totalRead;
            var limited = new PrefixWriteStream(output, allowed);
            var uploadProgress = progress is null ? null : new CumulativeProgress(offset, progress);
            var upload = session.GetStagedToStream(limited, uploadProgress, cancellationToken);
            upload.ThrowIfFailed();

            var written = limited.Written;
            offset += stagedBytes;
            totalRead += written;
            progress?.Report(totalRead);
            if (written == 0)
            {
                break;
            }
        }

        return totalRead;
    }

    /// <summary>Rejects partition names that could corrupt or redirect the <c>oem read</c> command line before anything is sent to the device. <para>在向设备发送任何内容之前拒绝可能破坏或劫持 <c>oem read</c> 命令行的分区名。</para></summary>
    private static void ValidatePartitionName(string partition)
    {
        if (partition.Length == 0)
        {
            throw new ArgumentException("Partition name must not be empty.", nameof(partition));
        }

        if (partition.Length > MaxPartitionNameLength)
        {
            throw new ArgumentException(
                $"Partition name '{partition}' exceeds the {MaxPartitionNameLength}-character limit.", nameof(partition));
        }

        foreach (var c in partition)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException(
                    $"Partition name '{partition}' contains whitespace or control characters.", nameof(partition));
            }
        }
    }

    /// <summary>Parses the staged byte count the device reports in the OKAY text: optional surrounding whitespace and an optional <c>0x</c> prefix, hexadecimal digits (some devices pad the field, others do not). <para>解析设备在 OKAY 文本中报告的暂存字节数：可选的首尾空白与可选 <c>0x</c> 前缀，其后为十六进制数字（部分设备补齐字段宽度，部分不补）。</para></summary>
    private static long ParseStagedSize(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Length > 0 &&
            long.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
            ? size
            : 0;
    }

    /// <summary>
    /// Progress adapter for one upload round: the session reports per-upload cumulative bytes, so
    /// the reader shifts them by the round's starting offset to keep the sink monotonic across rounds.
    /// <para>单轮上传的进度适配器：会话上报的是单次上传的累计字节，读取器以该轮起始偏移平移，
    /// 使进度在多轮之间保持单调。</para>
    /// </summary>
    private sealed class CumulativeProgress(long baseOffset, IProgress<long> sink) : IProgress<long>
    {
        public void Report(long value) => sink.Report(baseOffset + value);
    }

    /// <summary>
    /// Forwards only the first N bytes to the underlying output and discards the rest, so a device
    /// staging more bytes than <c>maximumBytes</c> cannot overflow the caller's stream while the
    /// whole staged payload is still drained from the wire.
    /// <para>只把前 N 字节转发到底层输出流并丢弃其余，使设备暂存超过 <c>maximumBytes</c> 时不会溢出
    /// 调用方的流，同时整段暂存负载仍从线路上被排空。</para>
    /// </summary>
    private sealed class PrefixWriteStream(Stream output, long limit) : Stream
    {
        private long _written;

        public long Written => _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var remaining = limit - _written;
            if (remaining <= 0 || count <= 0)
            {
                return;
            }

            var take = (int)Math.Min(count, remaining);
            output.Write(buffer, offset, take);
            _written += take;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }
    }
}