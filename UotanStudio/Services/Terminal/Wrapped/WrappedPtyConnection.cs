using System.Text;
using Porta.Pty;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>
/// A PTY connection proxy that filters user input through a <see cref="WrappedCommandSession"/>: the
/// terminal keeps reading real process output and writing real input, but everything typed into the
/// input side first visits the session, which decides what still reaches the shell. Display output
/// produced by wrapped commands goes straight into the terminal emulator, not through this stream.
/// <para>经 <see cref="WrappedCommandSession"/> 过滤用户输入的 PTY 连接代理：终端照常读取真实进程输出、
/// 写入真实输入，但输入侧的一切都先经过会话，由会话决定哪些仍送达 shell。包装命令产生的显示输出直接写进
/// 终端模拟器，不经过此流。</para>
/// </summary>
/// <param name="inner">The real PTY connection.<para>真实 PTY 连接。</para></param>
/// <param name="session">The interception session.<para>拦截会话。</para></param>
internal sealed class WrappedPtyConnection(IPtyConnection inner, WrappedCommandSession session) : IPtyConnection
{
    private readonly WrappedInputStream _input = new(inner.WriterStream, session);

    /// <inheritdoc/>
    public Stream ReaderStream => inner.ReaderStream;

    /// <inheritdoc/>
    public bool SupportsCancellableRead => inner.SupportsCancellableRead;

    /// <inheritdoc/>
    public Stream WriterStream => _input;

    /// <inheritdoc/>
    public int Pid => inner.Pid;

    /// <inheritdoc/>
    public int ExitCode => inner.ExitCode;

    /// <inheritdoc/>
    public int ExitSignal => inner.ExitSignal;

    /// <inheritdoc/>
    public bool HasExited => inner.HasExited;

    /// <inheritdoc/>
    public bool WaitForExit(int milliseconds) => inner.WaitForExit(milliseconds);

    /// <inheritdoc/>
    public void Kill() => inner.Kill();

    /// <inheritdoc/>
    public Task KillAsync(bool entireProcessTree, TimeSpan? timeout) => inner.KillAsync(entireProcessTree, timeout);

    /// <inheritdoc/>
    public void Resize(int columns, int rows) => inner.Resize(columns, rows);

    /// <inheritdoc/>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited
    {
        add => inner.ProcessExited += value;
        remove => inner.ProcessExited -= value;
    }

    /// <inheritdoc/>
    public void Dispose() => inner.Dispose();

    private sealed class WrappedInputStream(Stream inner, WrappedCommandSession session) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override async Task FlushAsync(CancellationToken cancellationToken) =>
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The wrapped terminal input stream is async only.");

        public override async Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(buffer, offset, count);
            await session.DispatchAsync(text).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(buffer.Span);
            await session.DispatchAsync(text).ConfigureAwait(false);
        }
    }
}