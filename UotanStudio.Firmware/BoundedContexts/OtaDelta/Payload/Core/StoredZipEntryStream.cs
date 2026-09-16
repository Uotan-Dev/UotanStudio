using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;

/// <summary>
/// Resolves the raw data range of a STORED (uncompressed) entry inside a ZIP archive so the
/// payload bytes can be read directly from the ZIP file without materializing a temporary copy.
/// Any structural anomaly, missing entry, compressed/encrypted entry, or overflow falls back with
/// <see langword="false"/>; the caller then materializes the payload via the ZIP stream instead.
/// <para>用于解析 ZIP 归档内 STORED（未压缩）条目的原始数据范围，使 payload 字节可直接从 ZIP 文件
/// 读取，而无需物化临时副本。任何结构异常、条目缺失、压缩/加密条目或溢出均以 <see langword="false"/>
/// 回退；此时调用方改用 ZIP 流物化 payload。</para>
/// </summary>
internal static class StoredZipEntryReader
{
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const uint CentralDirectorySignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;

    private const ushort Zip64ExtraId = 0x0001;
    private const ushort LargeEntryCountMarker = 0xFFFF;
    private const uint MaxUIntMarker = 0xFFFFFFFF;
    private const int MaxEocdSearchTail = 65557;
    private const int IoBufferSize = 1 << 16;

    /// <summary>
    /// Tries to resolve the raw byte range of an entry inside the ZIP archive at
    /// <paramref name="zipPath"/>. Returns <see langword="true"/> only when the entry is found and
    /// stored uncompressed; <paramref name="dataOffset"/> is the file offset of the entry payload
    /// data and <paramref name="length"/> its exact size.
    /// <para>尝试解析 <paramref name="zipPath"/> 归档内某个条目的原始字节范围。仅当条目存在且以
    /// STORED（未压缩）方式存储时返回 <see langword="true"/>；<paramref name="dataOffset"/> 为条目
    /// 数据的文件偏移，<paramref name="length"/> 为其精确长度。</para>
    /// </summary>
    internal static bool TryResolve(string zipPath, string entryName, out long dataOffset, out long length)
    {
        dataOffset = 0;
        length = 0;
        if (string.IsNullOrWhiteSpace(zipPath) || string.IsNullOrEmpty(entryName))
        {
            return false;
        }

        try
        {
            using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferSize, FileOptions.RandomAccess);
            return TryResolveCore(zip, entryName, out dataOffset, out length);
        }
        catch
        {
            // 任何解析异常都落到物化回退路径，不把 ZIP 结构风险带入主解压流程。
            // Any parse error falls back to materialization; do not leak ZIP structure risk upstream.
            return false;
        }
    }

    /// <summary>
    /// Opens a seekable read-only view over the raw payload bytes of a STORED entry resolved by
    /// <see cref="TryResolve"/>. Callers must supply a fresh view per partition.
    /// <para>在 <see cref="TryResolve"/> 解析出的 STORED 条目原始字节上打开一个可定位只读视图。
    /// 调用方须为每个分区提供独立的视图实例。</para>
    /// </summary>
    internal static Stream OpenView(string zipPath, long dataOffset, long length)
        => new StoredZipEntryStream(
            new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferSize, FileOptions.RandomAccess),
            dataOffset,
            length);

    private static bool TryResolveCore(Stream zip, string entryName, out long dataOffset, out long length)
    {
        dataOffset = 0;
        length = 0;

        if (!TryLocateEndOfCentralDirectory(zip, out var eocd))
        {
            return false;
        }

        int totalEntries = ReadUInt16(zip, eocd + 10);
        var cdSize = ReadUInt32(zip, eocd + 12);
        var cdOffset = ReadUInt32(zip, eocd + 16);

        long cdStart = cdOffset;
        if (cdSize == MaxUIntMarker || cdOffset == MaxUIntMarker || totalEntries == LargeEntryCountMarker)
        {
            if (!TryReadZip64EndOfCentralDirectory(zip, eocd, out totalEntries, out cdStart))
            {
                return false;
            }
        }

        long cursor = cdStart;
        for (var i = 0; i < totalEntries; i++)
        {
            if (!TryReadCentralDirectoryEntry(zip, cursor, entryName, out var next, out var localHeaderOffset, out var matched))
            {
                return false;
            }

            if (matched)
            {
                return TryResolveLocalFileHeader(zip, localHeaderOffset, out dataOffset, out length);
            }

            cursor = next;
        }

        return false;
    }

    private static bool TryLocateEndOfCentralDirectory(Stream zip, out long eocd)
    {
        eocd = -1;
        var length = zip.Length;
        if (length < 22)
        {
            return false;
        }

        var tailLength = (int)Math.Min(length, MaxEocdSearchTail);
        var tailStart = length - tailLength;
        var tail = new byte[tailLength];
        ReadAt(zip, tailStart, tail);

        // 从末尾向前定位 EOCD 签名：距文件末尾须恰好留有注释区长度（0..MaxUInt16）。
        // Scan backwards for the EOCD signature, which must leave only the comment length at EOF.
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2));
            if (checked(tailStart + i + 22 + commentLength) == length)
            {
                eocd = tailStart + i;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadZip64EndOfCentralDirectory(Stream zip, long eocd, out int totalEntries, out long cdOffset)
    {
        totalEntries = 0;
        cdOffset = 0;

        var locator = eocd - 20;
        if (locator < 0 || ReadUInt32(zip, locator) != Zip64LocatorSignature)
        {
            return false;
        }

        var zip64RecordOffset = checked((long)ReadUInt64(zip, locator + 8));
        if (zip64RecordOffset < 0 || zip64RecordOffset + 56 > zip.Length
            || ReadUInt32(zip, zip64RecordOffset) != Zip64EndOfCentralDirectorySignature)
        {
            return false;
        }

        var total = ReadUInt64(zip, zip64RecordOffset + 32);
        var cdStart = ReadUInt64(zip, zip64RecordOffset + 48);
        if (total > int.MaxValue || cdStart > long.MaxValue)
        {
            return false;
        }

        totalEntries = (int)total;
        cdOffset = (long)cdStart;
        return true;
    }

    /// <summary>
    /// Parses one central directory entry at <paramref name="position"/>. <paramref name="next"/> is
    /// the offset of the following entry, <paramref name="localHeaderOffset"/> the matched entry's
    /// local header offset, and <paramref name="matched"/> is <see langword="true"/> when the entry
    /// name equals the requested payload entry.
    /// <para>解析 <paramref name="position"/> 处的一个中央目录条目。<paramref name="next"/> 为下一
    /// 个条目的偏移，<paramref name="localHeaderOffset"/> 为匹配条目的本地头偏移，当条目名等于请求的
    /// payload 条目时 <paramref name="matched"/> 为 <see langword="true"/>。</para>
    /// </summary>
    private static bool TryReadCentralDirectoryEntry(Stream zip, long position, string entryName, out long next,
        out long localHeaderOffset, out bool matched)
    {
        next = 0;
        localHeaderOffset = 0;
        matched = false;
        if (position < 0 || position + 46 > zip.Length)
        {
            return false;
        }

        if (ReadUInt32(zip, position) != CentralDirectorySignature)
        {
            return false;
        }

        var nameLength = ReadUInt16(zip, position + 28);
        var extraLength = ReadUInt16(zip, position + 30);
        var commentLength = ReadUInt16(zip, position + 32);

        var compressedSize = ReadUInt32(zip, position + 20);
        var uncompressedSize = ReadUInt32(zip, position + 24);
        var headerOffset = ReadUInt32(zip, position + 42);

        if (compressedSize == MaxUIntMarker || uncompressedSize == MaxUIntMarker || headerOffset == MaxUIntMarker)
        {
            if (!TryReadZip64EntryFields(zip, position + 46 + nameLength, extraLength,
                    ref compressedSize, ref uncompressedSize, ref headerOffset))
            {
                return false;
            }
        }

        var entryEnd = checked(position + 46 + nameLength + extraLength + commentLength);
        if (entryEnd > zip.Length || headerOffset > int.MaxValue)
        {
            return false;
        }

        var name = ReadName(zip, position + 46, nameLength);
        next = entryEnd;
        localHeaderOffset = headerOffset;
        matched = string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool TryReadZip64EntryFields(Stream zip, long extraStart, int extraLength,
        ref uint compressedSize, ref uint uncompressedSize, ref uint localHeaderOffset)
    {
        // Zip64 扩展区：字段按未压缩长度、压缩长度、本地头偏移、起始盘号顺序出现。
        // Zip64 extra field order: uncompressed, compressed, local header offset, starting disk.
        var cursor = extraStart;
        var extraEnd = checked(extraStart + extraLength);
        while (cursor + 4 <= extraEnd)
        {
            var id = ReadUInt16(zip, cursor);
            var dataSize = ReadUInt16(zip, cursor + 2);
            var dataStart = checked(cursor + 4);
            if (dataStart + dataSize > extraEnd)
            {
                return false;
            }

            if (id == Zip64ExtraId)
            {
                var data = new byte[dataSize];
                ReadAt(zip, dataStart, data);
                var offset = 0;
                if (uncompressedSize == MaxUIntMarker)
                {
                    if (offset + 8 > dataSize) return false;
                    uncompressedSize = ReadUInt64WithinUInt32(data, offset);
                    offset += 8;
                }
                if (compressedSize == MaxUIntMarker)
                {
                    if (offset + 8 > dataSize) return false;
                    compressedSize = ReadUInt64WithinUInt32(data, offset);
                    offset += 8;
                }
                if (localHeaderOffset == MaxUIntMarker)
                {
                    if (offset + 8 > dataSize) return false;
                    localHeaderOffset = ReadUInt64WithinUInt32(data, offset);
                }

                return true;
            }

            cursor = checked(dataStart + dataSize);
        }

        return false;
    }

    private static uint ReadUInt64WithinUInt32(byte[] data, int offset)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
        if (value > uint.MaxValue)
        {
            throw new OverflowException("ZIP64 size exceeds the 32-bit range.");
        }

        return (uint)value;
    }

    private static bool TryResolveLocalFileHeader(Stream zip, long position, out long dataOffset, out long length)
    {
        dataOffset = 0;
        length = 0;
        if (position < 0 || position + 30 > zip.Length)
        {
            return false;
        }

        if (ReadUInt32(zip, position) != LocalFileHeaderSignature)
        {
            return false;
        }

        var flags = ReadUInt16(zip, position + 6);
        var method = ReadUInt16(zip, position + 8);
        var compressedSize = ReadUInt32(zip, position + 18);
        var nameLength = ReadUInt16(zip, position + 26);
        var extraLength = ReadUInt16(zip, position + 28);

        // STORED 条目：未加密（flags bit0 未置位）且未压缩（method == 0）。
        // STORED entry: unencrypted (flags bit 0 clear) and uncompressed (method == 0).
        if ((flags & 0x1) != 0 || method != 0)
        {
            return false;
        }

        var dataStart = checked(position + 30 + nameLength + extraLength);
        if (dataStart > zip.Length)
        {
            return false;
        }

        dataOffset = dataStart;
        length = compressedSize;
        return true;
    }

    private static ushort ReadUInt16(Stream zip, long offset)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadAt(zip, offset, buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static uint ReadUInt32(Stream zip, long offset)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadAt(zip, offset, buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(Stream zip, long offset)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadAt(zip, offset, buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static string ReadName(Stream zip, long offset, int length)
    {
        var bytes = new byte[length];
        ReadAt(zip, offset, bytes);
        return new string(Encoding.UTF8.GetChars(bytes));
    }

    private static void ReadAt(Stream zip, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > zip.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        zip.Position = offset;
        zip.ReadExactly(destination);
    }
}

/// <summary>
/// A seekable read-only stream that exposes the raw bytes of a STORED ZIP entry at
/// <paramref name="dataOffset"/> within a supplied base stream. Logical positions are mapped to
/// <c>dataOffset + logicalPosition</c>, so zero-copy direct reads against the ZIP file are possible.
/// <para>一个可定位只读流，暴露在给定基流中 <paramref name="dataOffset"/> 处 STORED ZIP 条目的原始
/// 字节。逻辑位置被映射为 <c>dataOffset + 逻辑位置</c>，从而可以直接对 ZIP 文件做零拷贝读取。</para>
/// </summary>
internal sealed class StoredZipEntryStream : Stream
{
    private readonly Stream _base;
    private readonly long _dataOffset;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Initializes a new read-only view over a STORED entry's raw bytes.
    /// <para>在 STORED 条目的原始字节上初始化一个只读视图。</para>
    /// </summary>
    /// <param name="baseStream">The seekable stream backing the ZIP file; owned and disposed by this view.
    /// <para>底层可定位流（承载 ZIP 文件）；由本视图持有并在释放时关闭。</para></param>
    /// <param name="dataOffset">Zero-based file offset where the entry payload data begins.
    /// <para>条目有效负载数据起始处的从零开始的文件偏移。</para></param>
    /// <param name="length">The exact length of the entry payload data.
    /// <para>条目有效负载数据的精确长度。</para></param>
    internal StoredZipEntryStream(Stream baseStream, long dataOffset, long length)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (dataOffset < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataOffset), "Data offset and length must be non-negative.");
        }

        _base = baseStream;
        _dataOffset = dataOffset;
        _length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            ThrowIfDisposed();
            _position = value;
        }
    }

    /// <summary>
    /// Reads bytes while mapping the logical position into the underlying ZIP file.
    /// <para>把逻辑位置映射到底层 ZIP 文件后读取字节。</para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var toRead = (int)Math.Min(buffer.Length, _length - _position);
        if (toRead <= 0)
        {
            return 0;
        }

        _base.Position = _dataOffset + _position;
        var read = _base.Read(buffer[..toRead]);
        _position = checked(_position + read);
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0 || newPosition > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _position = newPosition;
        return _position;
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush()
    {
        // 视图是只读的，无需刷新。
        // The view is read-only; there is nothing to flush.
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _base.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StoredZipEntryStream));
        }
    }
}