using System.Collections;
using System.Globalization;
using System.Text;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>十六进制对比行的懒加载集合：按行索引按需读取两个文件并构造
/// <see cref="CompareRowViewModel"/>（偏移/十六进制串/ASCII 串），供虚拟化 <c>ListBox</c>
/// 随机索引取行；内置小窗口缓存与统一的锁，支持 UI 与后台并发访问。
/// 每行字节数常量独立定义，不引用 <c>Views/Controls/HexMetrics</c>，
/// 保持 ViewModels 层不依赖 Views 层。</para>
/// Lazily loaded hex-compare row collection: reads both files by row index on demand to build
/// <see cref="CompareRowViewModel"/> rows (offset/hex string/ASCII string) for a virtualized
/// <c>ListBox</c>; a small window cache and a single lock make random access fast and safe for
/// concurrent UI/background use. The bytes-per-line constant is defined here independently and does
/// not reference <c>Views/Controls/HexMetrics</c>, keeping the ViewModels layer free of Views
/// dependencies.
/// </summary>
public sealed class CompareHexRowCollection : IReadOnlyList<CompareRowViewModel>, IList, IDisposable
{
    /// <summary>
    /// <para>每行字节数。</para>
    /// Bytes per row.
    /// </summary>
    private const int BytesPerLine = 16;

    /// <summary>
    /// <para>窗口缓存容量：保留最近访问的 256 行的字节，命中时避免重复 seek 与读取。</para>
    /// Window-cache capacity: keeps the bytes of the 256 most recently accessed rows so cache hits
    /// avoid repeated seeks and reads.
    /// </summary>
    private const int CacheCapacity = 256;

    private readonly FileStream _leftStream;
    private readonly FileStream _rightStream;
    private readonly long _leftLength;
    private readonly long _rightLength;
    private readonly int _count;
    private readonly byte[] _rowDiffBitmap;
    private readonly object _syncRoot = new();
    private readonly CacheSlot[] _cache = new CacheSlot[CacheCapacity];

    /// <summary>
    /// <para>创建十六进制对比行集合：立即打开两个文件流、捕获文件长度并计算总行数，
    /// 行字节在首次索引访问时懒加载读取。</para>
    /// Creates the hex-compare row collection: opens both file streams immediately, captures the
    /// file lengths and computes the total row count, while row bytes are lazily read on the first
    /// access to each index.
    /// </summary>
    /// <param name="leftPath">左文件路径。<para>Left file path.</para></param>
    /// <param name="rightPath">右文件路径。<para>Right file path.</para></param>
    /// <param name="rowDiffBitmap">行差异位图（每 16 字节行 1 bit，由
    /// <see cref="BinaryDiffService.CompareStats(string, string, byte[], CancellationToken)"/> 生成）；
    /// 长度必须不小于 <see cref="BinaryDiffService.RowBitmapLength"/> 的返回值。</param>
    /// <param name="rowDiffBitmap">Row-diff bitmap (1 bit per 16-byte row, produced by
    /// <see cref="BinaryDiffService.CompareStats(string, string, byte[], CancellationToken)"/>);
    /// its length must be at least <see cref="BinaryDiffService.RowBitmapLength"/>'s return value.</param>
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
    /// <para>总行数：按两侧文件较长者以 16 字节一行向上取整。</para>
    /// Total row count: the longer of the two files rounded up to whole 16-byte rows.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// <para>获取指定行的对比行视图模型；行数据按需从两个文件读取（带最近 256 行窗口缓存），
    /// 索引器可能被 UI 与后台线程同时调用，统一由同一把锁保护。</para>
    /// Gets the compare row for the given index; row data is read from both files on demand (with a
    /// 256-row window cache), and the indexer is safe to call concurrently from the UI and
    /// background threads as a single lock guards all access.
    /// </summary>
    /// <param name="index">行索引（0 起）。<para>Row index (0-based).</para></param>
    /// <returns><para>该行的对比行视图模型。</para> The compare row for the index.</returns>
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
                    // 行 VM 随槽一并缓存：同一索引反复访问（滚动/虚拟化重新取行/两侧共享同一集合）
                    // 直接复用实例，不再每次分配新 VM 与字符串。
                    // The row VM is cached with the slot: repeated access to the same index (scrolling,
                    // virtualizer re-fetching, both panes sharing the collection) reuses the instance
                    // instead of allocating a new VM and strings every time.
                    slot.Row = BuildRow(index, slot);
                }

                return slot.Row!;
            }
        }
    }

    /// <summary>
    /// <para>逐行懒加载地枚举全部对比行。</para>
    /// Enumerates every compare row, loading each row lazily.
    /// </summary>
    public IEnumerator<CompareRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    /// <summary>
    /// <para>非泛型枚举器实现，转发到 <see cref="GetEnumerator"/>。</para>
    /// Non-generic enumerator implementation forwarding to <see cref="GetEnumerator"/>.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- 非泛型 IList 只读实现 ----
    // Avalonia 的 ItemsSourceView.Create 只对非泛型 IList 或 IReadOnlyList<object?> 采用可虚拟化
    // 的 ListSourceView；仅实现泛型 IReadOnlyList<T> 的自定义集合（泛型不变性）会落入
    // EnumerableSourceView——无 O(1) 索引访问，VirtualizingStackPanel 只能逐行物化，几十万行即
    // 产生 GB 级内存。实现只读 IList 即可让 hex 列表像文本/文件夹模式（List<T>）一样虚拟化。
    // ---- Read-only non-generic IList ----
    // Avalonia's ItemsSourceView.Create only picks the virtualizable ListSourceView for the
    // non-generic IList or IReadOnlyList<object?>; a custom collection implementing only the generic
    // IReadOnlyList<T> (generic invariance) falls into EnumerableSourceView — no O(1) indexed access,
    // so the VirtualizingStackPanel materializes every row, yielding gigabytes for hundreds of
    // thousands of rows. Implementing a read-only IList lets the hex list virtualize like the
    // text/folder modes (List<T>).

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
    /// <para>按引用查找行 VM 的索引（集合复用缓存实例，同一索引始终返回同一引用）。</para>
    /// Finds a row VM's index by reference (the collection reuses cached instances, so the same
    /// index always returns the same reference).
    /// </summary>
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
    /// <para>释放内部持有的两个文件流。</para>
    /// Releases the two file streams held internally.
    /// </summary>
    public void Dispose()
    {
        _leftStream.Dispose();
        _rightStream.Dispose();
    }

    /// <summary>
    /// <para>按行索引与缓存槽中的数据构造对比行：偏移文本、十六进制字节串与 ASCII 串，
    /// 缺失侧对应字段为空串；状态与高亮按 diffRanges 相交、仅左、仅右、相同的优先级判定。</para>
    /// Builds a compare row from the row index and the cache slot: offset text, hex byte string and
    /// ASCII string, with the missing side's fields empty; status and highlight follow the
    /// diff-intersection, right-only, left-only, equal priority order.
    /// </summary>
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
            isDirectory: false,
            depth: 0,
            leftLine: leftMissing ? string.Empty : offsetText,
            leftText: FormatHex(slot.Left, slot.LeftCount),
            leftSub: FormatAscii(slot.Left, slot.LeftCount),
            isLeftDiff,
            rightLine: rightMissing ? string.Empty : offsetText,
            rightText: FormatHex(slot.Right, slot.RightCount),
            rightSub: FormatAscii(slot.Right, slot.RightCount),
            isRightDiff);
    }

    /// <summary>
    /// <para>判断给定行起始偏移所在的行是否含差异：行索引 = 起始偏移 / 16，位图字节 = 行 / 8，
    /// 位 = 行 % 8；超出位图覆盖范围的行（两侧文件都缺失的尾部空行）返回 false。</para>
    /// Determines whether the row at the given start offset contains a difference: row index =
    /// start / 16, bitmap byte = row / 8, bit = row % 8; trailing rows beyond the bitmap's coverage
    /// (rows past both files' ends) return false.
    /// </summary>
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
    /// <para>从文件流读取一行的最多 16 字节到缓冲：先 seek 到行起始偏移，
    /// 短文件读到多少算多少；该行完全超出文件长度时返回 0。</para>
    /// Reads up to 16 bytes of one row from a stream into the buffer: seeks to the row start first
    /// and reads as many bytes as the shorter file provides; returns 0 when the row lies entirely
    /// beyond the end of the file.
    /// </summary>
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
    /// <para>把一行的字节格式化为两位大写的十六进制串，字节间以空格分隔（如 "0A FF 1B"）。</para>
    /// Formats a row's bytes as two-digit uppercase hex digits separated by spaces (e.g. "0A FF 1B").
    /// </summary>
    private static string FormatHex(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(count * 3);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(buffer[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// <para>把一行的字节格式化为 ASCII 串：可打印字符（0x20-0x7E）原样显示，其余用 '.' 代替。</para>
    /// Formats a row's bytes as an ASCII string: printable characters (0x20-0x7E) are shown as-is,
    /// everything else is replaced with '.'.
    /// </summary>
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
    /// <para>缓存槽：保存最近一次读取到的行字节数据与读取长度，命中时避免重复 seek/读取；
    /// 槽内字节缓冲被复用，配合统一的锁保护并发访问。</para>
    /// A cache slot holding the byte data and read counts of the last row read into it, so cache
    /// hits avoid repeated seeks and reads; its byte buffers are reused and guarded by the single
    /// lock for concurrent access.
    /// </summary>
    private sealed class CacheSlot
    {
        /// <summary>
        /// <para>槽内缓存的行索引；-1 表示未填充。</para>
        /// Row index cached in this slot; -1 when not yet populated.
        /// </summary>
        public long Index = -1;

        /// <summary>
        /// <para>左侧实际读取到的字节数。</para>
        /// Number of bytes actually read on the left side.
        /// </summary>
        public int LeftCount;

        /// <summary>
        /// <para>右侧实际读取到的字节数。</para>
        /// Number of bytes actually read on the right side.
        /// </summary>
        public int RightCount;

        /// <summary>
        /// <para>左侧行字节缓冲。</para>
        /// Left row byte buffer.
        /// </summary>
        public byte[] Left = new byte[BytesPerLine];

        /// <summary>
        /// <para>右侧行字节缓冲。</para>
        /// Right row byte buffer.
        /// </summary>
        public byte[] Right = new byte[BytesPerLine];

        /// <summary>
        /// <para>由本槽字节构造的行 VM；随槽缓存复用，索引命中时直接返回，不再重新分配。</para>
        /// The row VM built from this slot's bytes; cached with the slot so a cache hit returns it
        /// directly without allocating again.
        /// </summary>
        public CompareRowViewModel? Row;
    }
}