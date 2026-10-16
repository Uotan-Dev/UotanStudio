using System.Runtime.ExceptionServices;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Compares streamed device data using bounded memory, deferring comparison failures until the response is consumed.
/// <para>以有界内存比较设备数据，待响应消费完毕后才报告比较失败。</para>
/// </summary>
internal sealed class ReadBackComparisonStream(Stream source, string filename) : Stream
{
    private readonly byte[] _buffer = new byte[65536];
    private long _sourceLength;
    private long _sourceRead;
    private long _received;
    private long _mismatchOffset = -1;
    private byte _expected, _actual;
    private bool _initialized, _sourceEnded;
    private ExceptionDispatchInfo? _sourceError;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _received;
    public override long Position { get => _received; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = Math.Min(_buffer.Length, buffer.Length - offset);
            var read = await ReadSourceAsync(count, cancellationToken).ConfigureAwait(false);
            if (_mismatchOffset < 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (_buffer[i] == buffer.Span[offset + i]) continue;
                    _mismatchOffset = checked(_received + offset + i);
                    _expected = _buffer[i];
                    _actual = buffer.Span[offset + i];
                    break;
                }
            }
            offset += count;
        }
        _received = checked(_received + buffer.Length);
    }

    private async ValueTask<int> ReadSourceAsync(int count, CancellationToken cancellationToken)
    {
        if (_sourceError is not null || _sourceEnded) return 0;
        try
        {
            if (!_initialized)
            {
                source.Position = 0;
                _sourceLength = source.Length;
                _initialized = true;
            }
            count = (int)Math.Min(count, _sourceLength - _sourceRead);
            var read = 0;
            while (read < count)
            {
                var n = await source.ReadAsync(_buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                if (n == 0) { _sourceEnded = true; break; }
                read += n;
                _sourceRead += n;
            }
            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _sourceError = ExceptionDispatchInfo.Capture(ex);
            return 0;
        }
    }

    /// <summary>
    /// Finishes reading the source and reports size differences before byte differences, leaving the source open.
    /// <para>完成源读取，优先报告长度差异再报告字节差异，保持源流打开。</para>
    /// </summary>
    internal async Task CompleteAsync(CancellationToken cancellationToken)
    {
        while (await ReadSourceAsync(_buffer.Length, cancellationToken).ConfigureAwait(false) != 0) { }
        cancellationToken.ThrowIfCancellationRequested();
        _sourceError?.Throw();
        if (_sourceRead != _received)
            throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                $"Read-back verification size mismatch for {filename}: expected {_sourceRead}, got {_received}");
        if (_mismatchOffset >= 0)
            throw new EdlException(EdlErrorCode.OnTargetVerificationFailed,
                $"Read-back verification mismatch for {filename} at byte {_mismatchOffset}: expected 0x{_expected:X2}, got 0x{_actual:X2}");
    }
}