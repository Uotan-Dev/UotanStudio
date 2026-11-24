using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Firmware.BlockImage.Sparse.IO;

/// <summary>
/// Pull-style read stream that renders one <see cref="SparseFile"/> into exact Android sparse-container bytes
/// using only bounded buffers. It is byte-for-byte equivalent to <see cref="SparseWriter.WriteToStream"/> with
/// <c>sparse: true</c>, but never materializes the container on disk or in a large array: RAW payloads are read
/// from their lazy providers directly into the caller's buffer as the stream is drained, so peak memory stays
/// bounded by the caller's read size.
/// <para>以拉式流按需求把一个 <see cref="SparseFile"/> 渲染为精确的 Android 稀疏容器字节，仅使用有界缓冲区。
/// 它与 <c>sparse: true</c> 的 <see cref="SparseWriter.WriteToStream"/> 逐字节等价，但绝不把容器落盘或一次性装入大数组：
/// RAW 载荷在调用方读取流时从惰性提供器直接读入调用方缓冲区，因此峰值内存由调用方的读取大小界定。</para>
/// </summary>
public sealed class SparseContainerReadStream : Stream
{
    private abstract class Part(long length)
    {
        internal long Length { get; } = length;
    }

    private sealed class BytesPart(byte[] bytes) : Part(bytes.Length)
    {
        internal byte[] Bytes { get; } = bytes;
    }

    private sealed class RawPart(ISparseDataProvider provider, long length) : Part(length)
    {
        internal ISparseDataProvider Provider { get; } = provider;

        internal long Position { get; set; }
    }

    private readonly List<SparseChunk> _temporaryChunks = [];
    private readonly long _length;
    private readonly List<Part> _parts;
    private readonly int _partCount;
    private int _partIndex;
    private long _partOffset;
    private long _position;
    private bool _disposed;

    /// <summary>Creates a stream that renders <paramref name="sparseFile"/>; the model stays owned by the caller.</summary>
    /// <para>创建渲染 <paramref name="sparseFile"/> 的流；模型所有权仍归调用方。</para>
    /// <param name="sparseFile">Model to render. <para>要渲染的模型。</para></param>
    public SparseContainerReadStream(SparseFile sparseFile)
    {
        ArgumentNullException.ThrowIfNull(sparseFile);
        var plan = SparseWriter.Prepare(sparseFile, null, includeCrc: false, _temporaryChunks);
        var header = plan.Header;
        var parts = new List<Part>(plan.FinalChunks.Count * 2 + 2);

        var fileHeader = new byte[header.FileHeaderSize];
        header.WriteTo(fileHeader);
        parts.Add(new BytesPart(fileHeader));

        foreach (var chunk in plan.FinalChunks)
        {
            var expectedTotal = ChunkHelper.GetExpectedTotalSize(chunk.Header.ChunkType, chunk.Header.ChunkSize,
                header.ChunkHeaderSize, header.BlockSize);
            var headerToWrite = chunk.Header.TotalSize == expectedTotal
                ? chunk.Header
                : new ChunkHeader
                {
                    ChunkType = chunk.Header.ChunkType,
                    Reserved = chunk.Header.Reserved,
                    ChunkSize = chunk.Header.ChunkSize,
                    TotalSize = expectedTotal
                };
            parts.Add(new BytesPart(headerToWrite.ToBytes()));

            switch ((ChunkType)chunk.Header.ChunkType)
            {
                case ChunkType.Raw when chunk.DataProvider is not null:
                    parts.Add(new RawPart(chunk.DataProvider, (long)chunk.Header.ChunkSize * header.BlockSize));
                    break;
                case ChunkType.Fill:
                    var fill = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(fill, chunk.FillValue);
                    parts.Add(new BytesPart(fill));
                    break;
            }
        }

        if (plan.NeedsTrailingSkip)
        {
            var trailing = new ChunkHeader
            {
                ChunkType = (ushort)ChunkType.DontCare,
                ChunkSize = header.TotalBlocks - plan.SumBlocks,
                TotalSize = SparseFormat.ChunkHeaderSize
            };
            parts.Add(new BytesPart(trailing.ToBytes()));
        }

        _parts = parts;
        _partCount = parts.Count;
        _length = parts.Sum(static part => part.Length);
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <summary>Exact byte length of the rendered sparse container.</summary>
    /// <para>渲染出的稀疏容器的精确字节长度。</para>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length == 0 || _partIndex >= _partCount)
        {
            return 0;
        }

        var total = 0;
        while (total < destination.Length && _partIndex < _partCount)
        {
            var part = _parts[_partIndex];
            var remaining = part.Length - _partOffset;
            if (remaining <= 0)
            {
                _partIndex++;
                _partOffset = 0;
                continue;
            }

            var want = (int)Math.Min(destination.Length - total, remaining);
            switch (part)
            {
                case BytesPart bytes:
                    bytes.Bytes.AsSpan((int)_partOffset, want).CopyTo(destination[total..]);
                    break;
                case RawPart raw:
                    var read = raw.Provider.Read(raw.Position, destination.Slice(total, want));
                    if (read < want)
                    {
                        destination.Slice(total + read, want - read).Clear();
                    }
                    raw.Position += want;
                    break;
            }

            total += want;
            _partOffset += want;
        }

        _position += total;
        return total;
    }

    /// <inheritdoc/>
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            foreach (var chunk in _temporaryChunks)
            {
                chunk.Dispose();
            }
            _temporaryChunks.Clear();
        }
        base.Dispose(disposing);
    }
}