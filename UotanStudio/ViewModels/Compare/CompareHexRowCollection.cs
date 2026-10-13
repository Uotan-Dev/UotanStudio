using System.Collections;
using System.Globalization;
using System.Text;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Lazily loaded hex-comparison row collection: reads both files by row index on demand to build
/// offset/hex/ASCII rows for virtualized random access, with a small window cache and one lock.
/// <para>十六进制对比行的懒加载集合：按行索引按需读取两个文件并构造偏移/十六进制串/ASCII 串，
/// 支持虚拟化随机取行；内置小窗口缓存与统一的锁。</para>
/// </summary>
public sealed class CompareHexRowCollection : IReadOnlyList<CompareRowViewModel>, IList, IDisposable
{
    /// <summary>
    /// Bytes per row.
    /// <para>每行字节数。</para>
    /// </summary>
    private const int BytesPerLine = 16;

    /// <summary>
    /// Window-cache capacity: keeps the bytes of the 256 most recently accessed rows so cache hits
    /// avoid repeated seeks and reads.
    /// <para>窗口缓存容量：保留最近访问的 256 行的字节，命中时避免重复 seek 与读取。</para>
    /// </summary>
    private const int CacheCapacity = 256;

    private readonly FileStream _leftStream;
    private readonly FileStream _rightStream;
    private readonly long _leftLength;
    private readonly long _rightLength;
    private readonly int _count;
    private readonly byte[] _rowDiffBitmap;
    private readonly Lock _syncRoot = new();
    private readonly CacheSlot[] _cache = new CacheSlot[CacheCapacity];

    /// <summary>
    /// Creates the hex-comparison row collection: opens both file streams, captures the file lengths and
    /// computes the total row count; row bytes are read lazily on first access.
    /// <para>创建十六进制对比行集合：立即打开两个文件流、捕获文件长度并计算总行数，
    /// 行字节在首次索引访问时懒加载读取。</para>
    /// </summary>
    /// <param name="leftPath">Left file path. <para>左文件路径。</para></param>
    /// <param name="rightPath">Right file path. <para>右文件路径。</para></param>
    /// <param name="rowDiffBitmap">Row-diff bitmap, 1 bit per 16-byte row, sized by <see cref="BinaryDiffService.RowBitmapLength"/>. <para>行差异位图：每 16 字节行 1 位，长度按 <see cref="BinaryDiffService.RowBitmapLength"/> 计算。</para></param>
    public CompareHexRowCollection(string leftPath, string rightPath, byte[] rowDiffBitmap)
    {
        ArgumentNullException.ThrowIfNull(leftPath);
        ArgumentNullException.ThrowIfNull(rightPath);
        ArgumentNullException.ThrowIfNull(rowDiffBitmap);

        _rowDiffBitmap = rowDiffBitmap;
        try
        {
            _leftStream = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            _rightStream = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        }
        catch
        {
            _leftStream?.Dispose();
            _rightStream?.Dispose();
            throw;
        }

        _leftLength = _leftStream.Length;
        _rightLength = _rightStream.Length;
        _count = (int)((Math.Max(_leftLength, _rightLength) + BytesPerLine - 1) / BytesPerLine);

        var required = BinaryDiffService.RowBitmapLength(Math.Max(_leftLength, _rightLength));
        if (_rowDiffBitmap.Length < required)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowDiffBitmap), _rowDiffBitmap.Length,
                $"Row-diff bitmap must be at least {required} bytes for the given files.");
        }

        for (var i = 0; i < CacheCapacity; i++)
        {
            _cache[i] = new CacheSlot();
        }
    }

    /// <summary>
    /// Total row count: the longer of the two files rounded up to whole 16-byte rows.
    /// <para>总行数：按两侧文件较长者以 16 字节一行向上取整。</para>
    /// </summary>
    /// <value>The total number of comparison rows. <para>对比行的总数。</para></value>
    public int Count => _count;

    /// <summary>
    /// Gets the compare row for the given index; row data is read from both files on demand through a
    /// 256-row window cache guarded by a single lock.
    /// <para>获取指定索引的对比行；行数据按需从两个文件读取，经最近 256 行窗口缓存，由同一把锁保护。</para>
    /// </summary>
    /// <param name="index">Zero-based row index. <para>行索引（从零开始）。</para></param>
    /// <value>The compare row at the given index. <para>给定索引处的对比行。</para></value>
    public CompareRowViewModel this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);

            lock (_syncRoot)
            {
                var slot = _cache[index % CacheCapacity];
                if (slot.Index != index)
                {
                    var offset = (long)index * BytesPerLine;
                    slot.LeftCount = ReadLine(_leftStream, offset, _leftLength, slot.Left);
                    slot.RightCount = ReadLine(_rightStream, offset, _rightLength, slot.Right);
                    slot.Index = index;
                    slot.Row = BuildRow(index, slot);
                }

                return slot.Row!;
            }
        }
    }

    /// <summary>
    /// Enumerates every compare row, loading each row lazily.
    /// <para>逐行懒加载地枚举全部对比行。</para>
    /// </summary>
    /// <returns>The enumerator over every compare row. <para>遍历全部对比行的枚举器。</para></returns>
    public IEnumerator<CompareRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    /// <summary>
    /// Non-generic enumerator implementation forwarding to <see cref="GetEnumerator"/>.
    /// <para>非泛型枚举器实现，转发到 <see cref="GetEnumerator"/>。</para>
    /// </summary>
    /// <returns>The non-generic enumerator over every row. <para>遍历全部行的非泛型枚举器。</para></returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    bool IList.IsReadOnly => true;

    bool IList.IsFixedSize => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    int ICollection.Count => _count;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    bool IList.Contains(object? value) => value is CompareRowViewModel row && IndexOfReference(row) >= 0;

    int IList.IndexOf(object? value) => value is CompareRowViewModel row ? IndexOfReference(row) : -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < _count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    /// <summary>
    /// Finds a row's index by reference (the collection reuses cached instances, so the same
    /// index always returns the same reference).
    /// <para>按引用查找行的索引（集合复用缓存实例，同一索引始终返回同一引用）。</para>
    /// </summary>
    /// <param name="target">The row to locate by reference. <para>要按引用查找的行。</para></param>
    /// <returns>The index of the row, or -1 when it is not present. <para>该行的索引；不存在时为 -1。</para></returns>
    private int IndexOfReference(CompareRowViewModel target)
    {
        for (var i = 0; i < _count; i++)
        {
            if (ReferenceEquals(this[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Releases the two file streams held internally.
    /// <para>释放内部持有的两个文件流。</para>
    /// </summary>
    public void Dispose()
    {
        _leftStream.Dispose();
        _rightStream.Dispose();
    }

    /// <summary>
    /// Builds a compare row from the index and cache slot: offset, hex and ASCII texts; the missing side
    /// stays empty and status follows the diff, right-only, left-only, equal priority.
    /// <para>按行索引与缓存槽构造对比行：偏移、十六进制与 ASCII 文本，缺失侧字段为空；
    /// 状态按相交差异、仅右、仅左、相同的优先级判定。</para>
    /// </summary>
    /// <param name="index">The zero-based row index to build. <para>要构造的从零开始的行索引。</para></param>
    /// <param name="slot">The cache slot holding the row's bytes and read counts. <para>存放该行字节与读取长度的缓存槽。</para></param>
    /// <returns>The built compare row. <para>构造出的对比行。</para></returns>
    private CompareRowViewModel BuildRow(int index, CacheSlot slot)
    {
        var offset = (long)index * BytesPerLine;
        var leftMissing = offset >= _leftLength;
        var rightMissing = offset >= _rightLength;
        var hasDiff = IntersectsDiff(offset);

        FolderItemStatus status;
        bool isLeftDiff;
        bool isRightDiff;
        if (hasDiff)
        {
            status = FolderItemStatus.Different;
            isLeftDiff = true;
            isRightDiff = true;
        }
        else if (leftMissing)
        {
            status = FolderItemStatus.RightOnly;
            isLeftDiff = false;
            isRightDiff = false;
        }
        else if (rightMissing)
        {
            status = FolderItemStatus.LeftOnly;
            isLeftDiff = false;
            isRightDiff = false;
        }
        else
        {
            status = FolderItemStatus.Equal;
            isLeftDiff = false;
            isRightDiff = false;
        }

        var offsetText = offset.ToString("X8", CultureInfo.InvariantCulture);
        return new CompareRowViewModel(
            status,
            depth: 0,
            leftLine: leftMissing ? string.Empty : offsetText,
            leftText: FormatHex(slot.Left, slot.LeftCount),
            leftSub: FormatAscii(slot.Left, slot.LeftCount),
            isLeftDiff,
            rightLine: rightMissing ? string.Empty : offsetText,
            rightText: FormatHex(slot.Right, slot.RightCount),
            rightSub: FormatAscii(slot.Right, slot.RightCount),
            isRightDiff,
            hasLeftLine: !leftMissing,
            hasRightLine: !rightMissing);
    }

    /// <summary>
    /// Determines whether the row at the given start offset contains a difference; rows beyond the
    /// bitmap's coverage return false.
    /// <para>判断给定行起始偏移所在的行是否含差异；超出位图覆盖范围的行返回 false。</para>
    /// </summary>
    /// <param name="rowStart">The row's start offset in bytes. <para>该行在文件中的起始字节偏移。</para></param>
    /// <returns>Whether the row intersects a reported difference. <para>该行是否与已记录的差异相交。</para></returns>
    private bool IntersectsDiff(long rowStart)
    {
        var row = (int)(rowStart / BytesPerLine);
        if (row / 8 >= _rowDiffBitmap.Length)
        {
            return false;
        }

        return (_rowDiffBitmap[row / 8] & (1 << (row % 8))) != 0;
    }

    /// <summary>
    /// Reads up to 16 bytes of one row from a stream into the buffer, seeking to the row start first;
    /// returns 0 when the row lies entirely beyond the file end.
    /// <para>从文件流读取一行的最多 16 字节到缓冲，先 seek 到行起始偏移；
    /// 行完全超出文件长度时返回 0。</para>
    /// </summary>
    /// <param name="stream">The file stream to read from. <para>要读取的文件流。</para></param>
    /// <param name="offset">The row's start offset in the file. <para>该行在文件中的起始偏移。</para></param>
    /// <param name="length">The file length bounding the read. <para>限定本次读取的文件长度。</para></param>
    /// <param name="buffer">The buffer receiving the row's bytes. <para>接收该行字节的缓冲。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取到的字节数。</para></returns>
    private static int ReadLine(FileStream stream, long offset, long length, byte[] buffer)
    {
        if (offset >= length)
        {
            return 0;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var available = (int)Math.Min(BytesPerLine, length - offset);
        var total = 0;
        while (total < available)
        {
            var read = stream.Read(buffer, total, available - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Formats a row's bytes as two-digit uppercase hex digits separated by spaces, for example 0A FF 1B.
    /// <para>把一行的字节格式化为以空格分隔的两位大写十六进制串，例如 0A FF 1B。</para>
    /// </summary>
    /// <param name="buffer">The buffer holding the row's bytes. <para>存放该行字节的缓冲。</para></param>
    /// <param name="count">The number of valid bytes in the buffer. <para>缓冲内有效字节的数量。</para></param>
    /// <returns>The formatted hex string. <para>格式化后的十六进制串。</para></returns>
    private static string FormatHex(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        return string.Create((count * 3) - 1, (Buffer: buffer, Count: count), static (span, state) =>
        {
            for (var i = 0; i < state.Count; i++)
            {
                if (i > 0)
                {
                    span[(3 * i) - 1] = ' ';
                }

                state.Buffer[i].TryFormat(span[(3 * i)..], out _, "X2", CultureInfo.InvariantCulture);
            }
        });
    }

    /// <summary>
    /// Formats a row's bytes as an ASCII string: printable characters (0x20-0x7E) are shown as-is,
    /// everything else is replaced with '.'.
    /// <para>把一行的字节格式化为 ASCII 串：可打印字符（0x20-0x7E）原样显示，其余用 '.' 代替。</para>
    /// </summary>
    /// <param name="buffer">The buffer holding the row's bytes. <para>存放该行字节的缓冲。</para></param>
    /// <param name="count">The number of valid bytes in the buffer. <para>缓冲内有效字节的数量。</para></param>
    /// <returns>The formatted ASCII string. <para>格式化后的 ASCII 串。</para></returns>
    private static string FormatAscii(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
        {
            var b = buffer[i];
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        }

        return sb.ToString();
    }

    /// <summary>
    /// A cache slot holding the byte data and read counts of the last row read into it; its byte buffers
    /// are reused under the collection's single lock.
    /// <para>缓存槽：保存最近一次读取到的行字节数据与读取长度；槽内字节缓冲被复用，
    /// 配合统一的锁保护并发访问。</para>
    /// </summary>
    private sealed class CacheSlot
    {
        /// <summary>
        /// Row index cached in this slot; -1 when not yet populated.
        /// <para>槽内缓存的行索引；-1 表示未填充。</para>
        /// </summary>
        public long Index = -1;

        /// <summary>
        /// Number of bytes actually read on the left side.
        /// <para>左侧实际读取到的字节数。</para>
        /// </summary>
        public int LeftCount;

        /// <summary>
        /// Number of bytes actually read on the right side.
        /// <para>右侧实际读取到的字节数。</para>
        /// </summary>
        public int RightCount;

        /// <summary>
        /// Left row byte buffer.
        /// <para>左侧行字节缓冲。</para>
        /// </summary>
        public byte[] Left = new byte[BytesPerLine];

        /// <summary>
        /// Right row byte buffer.
        /// <para>右侧行字节缓冲。</para>
        /// </summary>
        public byte[] Right = new byte[BytesPerLine];

        /// <summary>
        /// The row VM built from this slot's bytes; cached with the slot so a cache hit returns it
        /// directly without allocating again.
        /// <para>由本槽字节构造的行 VM；随槽缓存复用，索引命中时直接返回，不再重新分配。</para>
        /// </summary>
        public CompareRowViewModel? Row;
    }
}