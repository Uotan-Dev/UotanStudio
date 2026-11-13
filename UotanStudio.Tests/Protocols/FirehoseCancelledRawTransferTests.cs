using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// A cancelled Firehose raw transfer cannot stop at an arbitrary byte: the programmer still owes the declared
/// payload and a final XML acknowledgement. These cases pin the quiesce that keeps the endpoint clean and the
/// target in command mode, plus the fallback that resets the endpoint when the link stops delivering.
/// <para>被取消的 Firehose 原始传输不能停在任意字节上：programmer 仍要发完声明的负载并给出最后的 XML 确认。
/// 这些用例固定住让端点保持干净、目标保持命令模式的收尾，以及链路不再供数时的降级复位。</para>
/// </summary>
public sealed class FirehoseCancelledRawTransferTests
{
    private const int SectorSize = 4096;
    private const int ChunkBytes = 4096;
    private const int Chunks = 8;
    private const int PayloadBytes = ChunkBytes * Chunks;

    [Fact]
    public async Task ReadPartitionToStreamAsync_CancelledMidPayload_DrainsRemainderAndConsumesCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));

        Assert.Equal(PayloadBytes, transport.RawBytesRead);
        Assert.Equal(2 * ChunkBytes, destination.Length);
        Assert.Equal(1, transport.CompletionReads);
        Assert.Equal(0, transport.ResetCalls);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_CancelledMidPayload_LeavesLinkReusableForNextCommand()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));
        var response = await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.False(response.IsRawMode);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_CompletionNeverArrives_ResetsEndpointAndRequiresReconnect()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2) { FailCompletion = true };
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));

        Assert.Equal(PayloadBytes, transport.RawBytesRead);
        Assert.Equal(1, transport.ResetCalls);
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramAsync_CancelledMidPayload_ZeroFillsRemainderAndConsumesCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 2, program: true);
        using var protocol = NewProtocol(transport);
        using var source = new MemoryStream(new byte[PayloadBytes]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ProgramAsync(
            source, "0", Chunks, "boot_a.bin", 4, cancellation.Token));

        Assert.Equal(PayloadBytes, transport.RawBytesWritten);
        Assert.Equal(1, transport.CompletionReads);
        Assert.Equal(0, transport.ResetCalls);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_LargeRemainder_IsDrainedToCompletionWithoutATimeCeiling()
    {
        // A cancelled read can never be stopped at an arbitrary byte, so the unwind is bounded by the payload the
        // target declared and not by a clock: this remainder is fifty times the cancel point and still has to be taken
        // back in full, because the target only returns to command mode after it.
        // 取消的读取无法停在任意字节，因此收尾由上界决定的是目标声明的负载而不是时钟：这里剩余量是取消点的五十倍，
        // 仍必须完整收回，因为目标只有发完才回到命令模式。
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 8, totalChunks: 512);
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();
        var reports = new List<EdlQuiesceProgress>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, 512, "super.img", 0, stoppingProgress: new SyncProgress(reports), cancellationToken: cancellation.Token));

        Assert.Equal(512 * ChunkBytes, transport.RawBytesRead);
        Assert.Equal(8 * ChunkBytes, (int)destination.Length);
        Assert.Equal(1, transport.CompletionReads);
        Assert.Equal(0, transport.ResetCalls);
        Assert.False(protocol.RequiresReconnect);
        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(512L * ChunkBytes, report.TotalBytes));
        Assert.True(reports[^1].DrainedBytes >= reports[0].DrainedBytes);
    }

    [Fact]
    public async Task ReadPartitionToStreamAsync_TargetStopsMidUnwind_ResetsEndpointAndRequiresReconnect()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellableTransfer(cancellation, cancelAfterChunks: 4) { StallAfterCancel = true };
        using var protocol = NewProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: cancellation.Token));

        Assert.Equal(4 * ChunkBytes, transport.RawBytesRead);
        Assert.Equal(0, transport.CompletionReads);
        Assert.Equal(1, transport.ResetCalls);
        Assert.True(protocol.RequiresReconnect);
    }

    /// <summary>
    /// A target that stops delivering is not a cancelled transfer: nobody pressed interrupt, the link simply went
    /// quiet mid-payload. The exit has to be the same one — reset the endpoint and demand a reconnect — because the
    /// bytes the target still owes cannot be drained and the next command would otherwise parse them.
    /// <para>停止供数的目标不是被取消的传输：没有人按下中断，只是链路在负载中途安静下来。出口必须与取消相同——
    /// 复位端点并要求重连——因为目标欠的字节无法排空，否则下一条命令会把它们当成协议数据解析。</para>
    /// </summary>
    [Fact]
    public async Task ReadPartitionToStreamAsync_TargetStopsMidPayload_ResetsEndpointAndRequiresReconnect()
    {
        var transport = new CancellableTransfer(new CancellationTokenSource(), cancelAfterChunks: 0)
        {
            StallAfterChunks = 2
        };
        using var protocol = NewProtocol(transport, silenceTimeoutMs: 200);
        using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<EdlException>(() => protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ReadPortTimeout, error.ErrorCode);
        Assert.Equal(2 * ChunkBytes, transport.RawBytesRead);
        Assert.Equal(2 * ChunkBytes, (int)destination.Length);
        Assert.Equal(0, transport.CompletionReads);
        Assert.Equal(1, transport.ResetCalls);
        Assert.True(protocol.RequiresReconnect);
    }

    /// <summary>
    /// The counterpart of the stall case: reads that return nothing are not by themselves a dead link. A slow target
    /// answers "nothing yet" at block boundaries and under back-pressure, so a transfer that keeps advancing between
    /// those empty reads has to finish. This is the case the old thirty-minute ceiling killed, because it bounded
    /// elapsed time instead of the absence of progress.
    /// <para>停摆用例的反面：单次读不到东西本身并不等于死链路。慢速目标在块边界和反压时会回答“暂时没有”，因此
    /// 在这些空读之间仍在推进的传输必须读完。这正是旧的 30 分钟上限会杀掉的情形，因为它界住的是经过的时间而不是
    /// 有没有进展。</para>
    /// </summary>
    [Fact]
    public async Task ReadPartitionToStreamAsync_EmptyReadsBetweenProgress_CompletesWithoutResetting()
    {
        var transport = new CancellableTransfer(new CancellationTokenSource(), cancelAfterChunks: 0)
        {
            TimeoutReadsBeforeData = 3
        };
        using var protocol = NewProtocol(transport, silenceTimeoutMs: 5_000);
        using var destination = new MemoryStream();

        var result = await protocol.ReadPartitionToStreamAsync(
            destination, 0, Chunks, "boot_a.bin", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.DeviceAcknowledged);
        Assert.Equal(PayloadBytes, transport.RawBytesRead);
        Assert.Equal(PayloadBytes, (int)destination.Length);
        Assert.Equal(1, transport.CompletionReads);
        Assert.Equal(0, transport.ResetCalls);
        Assert.False(protocol.RequiresReconnect);
    }

    /// <summary>
    /// A short write leaves the target waiting for a block whose accepted length is unknown, so the block cannot be
    /// replayed and the link cannot be reused. The write side now reaches the same conclusion as the read side.
    /// <para>短写让目标继续等待一个接收长度未知的块，因此该块无法重放、链路也无法复用。写入侧现在与读取侧得出同样的
    /// 结论。</para>
    /// </summary>
    [Fact]
    public async Task ProgramAsync_ShortWrite_ResetsEndpointAndRequiresReconnect()
    {
        var transport = new CancellableTransfer(new CancellationTokenSource(), cancelAfterChunks: 0, program: true)
        {
            ShortRawWrite = true
        };
        using var protocol = NewProtocol(transport, silenceTimeoutMs: 200);
        using var source = new MemoryStream(new byte[PayloadBytes]);

        var error = await Assert.ThrowsAsync<EdlException>(() => protocol.ProgramAsync(
            source, "0", Chunks, "boot_a.bin", 4, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.WritePortTimeout, error.ErrorCode);
        Assert.Equal(1, transport.ResetCalls);
        Assert.True(protocol.RequiresReconnect);
    }

    /// <summary>
    /// Records every report synchronously: <see cref="Progress{T}"/> posts to a captured context, so a test would
    /// otherwise inspect the list before anything reached it.
    /// <para>同步记录每次上报：<see cref="Progress{T}"/> 会投递到捕获的上下文，否则测试会在写入之前读到空列表。</para>
    /// </summary>
    private sealed class SyncProgress(List<EdlQuiesceProgress> reports) : IProgress<EdlQuiesceProgress>
    {
        public void Report(EdlQuiesceProgress value) => reports.Add(value);
    }

    private static FirehoseProtocol NewProtocol(ICommTransport transport, int silenceTimeoutMs = 60_000)
    {
        var protocol = new FirehoseProtocol(transport);
        protocol.Configuration.SectorSize = SectorSize;
        protocol.Configuration.MaxPayloadSizeFromTarget = ChunkBytes;
        protocol.Configuration.MaxPayloadSizeToTarget = ChunkBytes;
        protocol.Configuration.MaxPayloadSizeToTargetSupported = ChunkBytes;
        protocol.Configuration.MaxProgramChunkBytes = ChunkBytes;
        protocol.RawSilenceTimeoutMs = silenceTimeoutMs;
        return protocol;
    }

    private sealed class CancellableTransfer(CancellationTokenSource cancellation, int cancelAfterChunks,
        bool program = false, int totalChunks = Chunks)
        : ICommTransport
    {
        private readonly Queue<byte[]> _responses = new();
        private readonly int _payloadBytes = ChunkBytes * totalChunks;
        private int _deliveredRaw;
        private int _acceptedRaw;
        private int _commandWrites;
        private int _timeoutsServed;

        internal bool FailCompletion { get; init; }

        /// <summary>
        /// Gets or sets whether the link stops delivering once the transfer was cancelled, which is how a target that
        /// dies mid-unwind looks: nothing arrives and the completion never comes.
        /// <para>链路在传输被取消后是否停止供数，即目标在收尾中途死掉的样子：什么都收不到，完成应答也永远不来。</para>
        /// </summary>
        internal bool StallAfterCancel { get; init; }

        /// <summary>
        /// Gets or sets how many payload blocks the target delivers before it goes quiet without anyone cancelling,
        /// which is a target that dies on its own rather than an interrupted read.
        /// <para>目标在无人取消的情况下供数多少个负载块后安静下来，即自行死掉而不是被中断的读取。</para>
        /// </summary>
        internal int? StallAfterChunks { get; init; }

        /// <summary>
        /// Gets or sets how many reads answer with a timeout before the first byte arrives, which is how back-pressure
        /// and block boundaries look on a slow but healthy link.
        /// <para>首个字节到达前有多少次读取以超时回答，即慢但健康的链路上反压与块边界的样子。</para>
        /// </summary>
        internal int TimeoutReadsBeforeData { get; init; }

        /// <summary>
        /// Gets or sets whether raw writes are accepted one byte short, which is a transport that cannot say how much
        /// of the block the target really took.
        /// <para>原始写入是否少接受一个字节，即无法说明目标实际接收多少的传输。</para>
        /// </summary>
        internal bool ShortRawWrite { get; init; }

        internal int RawBytesRead => _deliveredRaw;

        internal int RawBytesWritten => _acceptedRaw;

        internal int CompletionReads { get; private set; }

        internal int ResetCalls { get; private set; }

        public bool IsConnected => true;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset() => ResetCalls++;

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count > 0)
            {
                return Task.FromResult(Copy(_responses.Dequeue(), buffer, offset));
            }

            if (_timeoutsServed < TimeoutReadsBeforeData)
            {
                _timeoutsServed++;
                throw new TimeoutException("The target answered with no bytes yet.");
            }

            if (StallAfterCancel && cancellation.IsCancellationRequested)
            {
                return Task.FromResult(0);
            }

            if (StallAfterChunks is { } stallLimit && _deliveredRaw >= stallLimit * ChunkBytes)
            {
                return Task.FromResult(0);
            }

            if (_deliveredRaw >= _payloadBytes)
            {
                if (FailCompletion)
                {
                    throw new IOException("The target never emitted the transfer completion.");
                }

                CompletionReads++;
                return Task.FromResult(Copy(Response(rawMode: false), buffer, offset));
            }

            var count = Math.Min(length, _payloadBytes - _deliveredRaw);
            buffer.AsSpan(offset, count).Clear();

            _deliveredRaw += count;
            if (!cancellation.IsCancellationRequested && cancelAfterChunks > 0
                && _deliveredRaw >= cancelAfterChunks * ChunkBytes)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(count);
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs) => length;

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length > 0 && data[offset] == (byte)'<')
            {
                _commandWrites++;
                _responses.Enqueue(Response(rawMode: _commandWrites == 1));
                return Task.FromResult((long)length);
            }

            _acceptedRaw += length;
            if (!program)
            {
                return Task.FromResult((long)length);
            }

            if (ShortRawWrite && length > 1)
            {
                return Task.FromResult((long)length - 1);
            }

            if (!cancellation.IsCancellationRequested && cancelAfterChunks > 0
                && _acceptedRaw >= cancelAfterChunks * ChunkBytes)
            {
                cancellation.Cancel();
            }

            return Task.FromResult((long)length);
        }

        private static int Copy(byte[] source, byte[] buffer, int offset)
        {
            Buffer.BlockCopy(source, 0, buffer, offset, source.Length);
            return source.Length;
        }

        private static byte[] Response(bool rawMode)
            => Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><response value=\"ACK\" rawmode=\"{(rawMode ? "true" : "false")}\" /></data>");

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}