using System.Threading.Channels;
using Porta.Pty;

namespace UotanStudio.Services.Terminal;

/// <summary>
/// Presents an in-process device shell relay as a PTY connection, so the terminal view consumes it
/// like any spawned shell while the host keeps its single ADB/HDC transport instead of a second
/// process opening a competing one.
/// <para>把进程内的设备 shell 转发以 PTY 连接的形式提供给终端视图：终端照常消费它，而宿主始终只保留
/// 一条 ADB/HDC 传输，不再由第二个进程另开一份竞争的传输。</para>
/// </summary>
internal sealed class DeviceShellConnection : IPtyConnection
{
    private readonly ShellByteChannel _toShell = new();
    private readonly ShellByteChannel _fromShell = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _relay;
    private int _exitCode;
    private int _disposed;

    /// <summary>
    /// Starts the relay immediately and exposes its two directions as the PTY streams.
    /// <para>立即启动转发，并把两个方向作为 PTY 流暴露。</para>
    /// </summary>
    /// <param name="relay">
    /// Receives the terminal-to-shell input stream, the shell-to-terminal output stream and the
    /// cancellation token; it completes when the remote shell ends.
    /// <para>接收终端到 shell 的输入流、shell 到终端的输出流与取消令牌；远端 shell 结束时完成。</para>
    /// </param>
    internal DeviceShellConnection(Func<Stream, Stream, CancellationToken, Task> relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ReaderStream = _fromShell.Output;
        WriterStream = _toShell.Input;
        _relay = Task.Run(() => RelayAsync(relay));
    }

    /// <inheritdoc/>
    public Stream ReaderStream { get; }

    /// <inheritdoc/>
    public Stream WriterStream { get; }

    /// <summary>
    /// Always <see langword="false"/>: the reader ends by completing the channel, not by cancelling
    /// the read, so closing the terminal never throws out of the terminal pump.
    /// <para>恒为 <see langword="false"/>：读取端以完成通道结束，而不是取消读取，因此关闭终端不会让终端泵抛异常。</para>
    /// </summary>
    public bool SupportsCancellableRead => false;

    /// <summary>No operating-system process backs this connection.<para>本连接背后没有操作系统进程。</para></summary>
    public int Pid => 0;

    /// <summary>The relay exit code, 0 while it is still running.<para>转发退出码；仍在运行时为 0。</para></summary>
    public int ExitCode => Volatile.Read(ref _exitCode);

    /// <summary>
    /// Raised once the relay ends, carrying the exit code; the terminal session uses this because
    /// Porta.Pty keeps the only constructor of <see cref="PtyExitedEventArgs"/> internal.
    /// <para>转发结束时触发并携带退出码；由于 Porta.Pty 把 <see cref="PtyExitedEventArgs"/> 的唯一
    /// 构造函数设为 internal，终端会话改用本事件。</para>
    /// </summary>
    internal event EventHandler<int>? Exited;

    /// <summary>
    /// Not raised: the framework event argument type cannot be constructed outside Porta.Pty, so
    /// completion is reported through <see cref="Exited"/>.
    /// <para>不会触发：该框架事件参数类型无法在 Porta.Pty 之外构造，结束改由 <see cref="Exited"/> 报告。</para>
    /// </summary>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public bool WaitForExit(int milliseconds) => _relay.Wait(milliseconds);

    /// <summary>Ends the relay, which closes the shell when the device honours cancellation.
    /// <para>结束转发；设备遵循取消时 shell 随之关闭。</para></summary>
    public void Kill()
    {
        _lifetime.Cancel();
        _toShell.Complete();
        _fromShell.Complete();
    }

    /// <summary>Accepts but ignores a terminal resize: a raw ADB shell has no resize protocol.
    /// <para>接受但忽略终端尺寸变更：裸 ADB shell 没有尺寸协议。</para></summary>
    /// <param name="columns">Requested columns.<para>请求的列数。</para></param>
    /// <param name="rows">Requested rows.<para>请求的行数。</para></param>
    public void Resize(int columns, int rows)
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();
        ReaderStream.Dispose();
        WriterStream.Dispose();
        _ = _relay.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
    }

    private async Task RelayAsync(Func<Stream, Stream, CancellationToken, Task> relay)
    {
        var exitCode = 0;
        try
        {
            await relay(_toShell.Output, _fromShell.Input, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            exitCode = 1;
            await _fromShell.WriteAsync("[UotanStudio] " + error.Message + "\r\n").ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _exitCode, exitCode);
            _toShell.Complete();
            _fromShell.Complete();
            Exited?.Invoke(this, exitCode);
        }
    }

}

/// <summary>
/// One direction of a byte stream with backpressure: the write side feeds chunks into a bounded
/// channel and the read side hands them to the consumer in order, reporting end of stream once the
/// channel is completed.
/// <para>字节流的单向管道（带背压）：写入端把分块送入有界通道，读取端按顺序交给消费方，通道完成后
/// 报告流结束。</para>
/// </summary>
internal sealed class ShellByteChannel
{
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private byte[]? _pending;
    private int _pendingOffset;

    internal ShellByteChannel()
    {
        Input = new WriteSide(this);
        Output = new ReadSide(this);
    }

    /// <summary>Gets the stream the producer writes to.<para>获取生产者写入的流。</para></summary>
    internal Stream Input { get; }

    /// <summary>Gets the stream the consumer reads from, ending at end of stream.
    /// <para>获取消费方读取的流，结束时返回流结束。</para></summary>
    internal Stream Output { get; }

    /// <summary>Marks the direction complete so the reader observes end of stream.
    /// <para>把该方向标记为完成，使读取端观察到流结束。</para></summary>
    internal void Complete() => _chunks.Writer.TryComplete();

    /// <summary>Writes UTF-8 text into the direction, ignoring a completed channel.
    /// <para>把 UTF-8 文本写入该方向；通道已完成时忽略。</para>
    /// </summary>
    /// <param name="text">Text to send to the reader.<para>发送给读取端的文本。</para></param>
    internal ValueTask WriteAsync(string text)
        => Input.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));

    private sealed class WriteSide(ShellByteChannel owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Write(ReadOnlySpan<byte> buffer)
            => WriteAsync(buffer.ToArray(), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return ValueTask.CompletedTask;
            }

            return WriteCoreAsync(buffer.ToArray(), cancellationToken);
        }

        private async ValueTask WriteCoreAsync(byte[] chunk, CancellationToken cancellationToken)
        {
            try
            {
                await owner._chunks.Writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ReadSide(ShellByteChannel owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var read = ReadAsync(temp.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (true)
            {
                if (owner._pending is { } pending)
                {
                    var take = Math.Min(buffer.Length, pending.Length - owner._pendingOffset);
                    pending.AsMemory(owner._pendingOffset, take).CopyTo(buffer);
                    owner._pendingOffset += take;
                    if (owner._pendingOffset >= pending.Length)
                    {
                        owner._pending = null;
                        owner._pendingOffset = 0;
                    }

                    return take;
                }

                bool available;
                try
                {
                    available = await owner._chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }

                if (!available || !owner._chunks.Reader.TryRead(out var chunk))
                {
                    return 0;
                }

                owner._pending = chunk;
                owner._pendingOffset = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Complete();
            }

            base.Dispose(disposing);
        }
    }
}