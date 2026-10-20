using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// A write-only stream that decouples a producer's writes from the file system: bytes are gathered into two
/// alternating pooled chunks and handed to <see cref="RandomAccess"/>, so the next chunk is being produced
/// while the previous one is still in flight. Chunk order and the final file bytes are identical to a
/// synchronous sequential write, because every chunk carries the absolute offset it belongs at.
/// <para>把生产者的写入与文件系统解耦的只写流：字节汇集到两块交替的池化分块后交给 <see cref="RandomAccess"/>，
/// 因此上一块仍在传输时下一块已经在生产。分块顺序与最终文件字节与同步顺序写入完全一致，因为每一块都携带
/// 它所属的绝对偏移。</para>
/// </summary>
/// <remarks>
/// Two chunks are enough to overlap one write with one produce step; more would only raise the peak memory. A
/// new chunk is started only after the previous write completed, so at most one write is in flight and the
/// buffer it uses can never be the one being filled. Both buffers live for the whole stream and are returned on
/// dispose, so a one-megabyte chunk costs no large-object-heap allocation per file.
/// <para>两块足以让一次写入与一次生产重叠；更多只会抬高峰值内存。只有上一次写入完成后才启动新分块，因此同一时刻
/// 至多一次写入在途，它使用的缓冲也绝不可能正是正在填充的那一块。两块缓冲在整个流生命周期内存活并在释放时归还，
/// 因此一兆字节的分块不会让每个文件产生大对象堆分配。</para>
/// </remarks>
internal sealed class OverlappedWriteStream : Stream
{
    private readonly FileStream _target;
    private readonly SafeFileHandle _handle;
    private readonly int _chunkSize;
    private readonly byte[] _first;
    private readonly byte[] _second;
    private Task? _pendingWrite;
    private int _fillIndex;
    private int _filled;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Creates an overlapped writer over an unbuffered file stream.
    /// <para>在无缓冲文件流之上创建重叠写入器。</para>
    /// </summary>
    /// <param name="target">Destination file opened unbuffered for writing; the caller keeps ownership.
    /// <para>以无缓冲方式打开用于写入的目标文件；所有权仍归调用方。</para></param>
    /// <param name="chunkSize">Bytes gathered per chunk before a write is started.
    /// <para>启动一次写入前每块汇集的字节数。</para></param>
    internal OverlappedWriteStream(FileStream target, int chunkSize)
    {
        _target = target;
        _handle = target.SafeFileHandle;
        _chunkSize = chunkSize;
        _first = ArrayPool<byte>.Shared.Rent(chunkSize);
        _second = ArrayPool<byte>.Shared.Rent(chunkSize);
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed;

    /// <inheritdoc />
    public override long Length => _position + _filled;

    /// <inheritdoc />
    public override long Position
    {
        get => _position + _filled;
        set => throw new NotSupportedException("The overlapped writer is append-only.");
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            if (_filled == _chunkSize)
            {
                StartChunk();
            }

            var take = Math.Min(_chunkSize - _filled, buffer.Length);
            buffer[..take].CopyTo(Current.Span[_filled..]);
            _filled += take;
            buffer = buffer[take..];
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        if (_disposed)
        {
            return;
        }

        if (_filled > 0)
        {
            StartChunk();
        }

        CompletePending();
        _target.Flush();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            try
            {
                Flush();
            }
            finally
            {
                _disposed = true;
                ArrayPool<byte>.Shared.Return(_first);
                ArrayPool<byte>.Shared.Return(_second);
            }
        }

        base.Dispose(disposing);
    }

    private Memory<byte> Current => _fillIndex == 0 ? _first.AsMemory(0, _chunkSize) : _second.AsMemory(0, _chunkSize);

    /// <summary>
    /// Starts writing the filled chunk at its absolute offset and flips to the other slot. The previous chunk is
    /// awaited first, so the buffer that is about to be filled is never one that is still being written.
    /// <para>把已填满分块写到它的绝对偏移并切换到另一槽。先等待上一分块，因此即将填充的缓冲绝不会仍在写入。</para>
    /// </summary>
    private void StartChunk()
    {
        CompletePending();
        var length = _filled;
        var offset = _position;
        _pendingWrite = RandomAccess.WriteAsync(_handle, Current[..length], offset).AsTask();
        _position += length;
        _filled = 0;
        _fillIndex ^= 1;
    }

    /// <summary>
    /// Waits for the in-flight chunk and surfaces its failure.
    /// <para>等待在途分块并传播其失败。</para>
    /// </summary>
    private void CompletePending()
    {
        if (_pendingWrite is null)
        {
            return;
        }

        _pendingWrite.GetAwaiter().GetResult();
        _pendingWrite = null;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}