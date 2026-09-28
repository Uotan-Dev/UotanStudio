using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Squashfs;

/// <summary>
/// Streams the metadata window chain of one SquashFS table (inode or directory). Windows are addressed by
/// their byte offset relative to the table start; each window carries a two-byte header followed by stored
/// or compressor-encoded bytes and expands to at most 8 KiB of table content.
/// <para>按序流出 SquashFS 单个表（inode 或 directory）的元数据窗口链。窗口以相对表起始的字节偏移寻址；
/// 每个窗口含两字节头，其后为存储或压缩编码字节，展开后最多承载 8 KiB 表内容。</para>
/// </summary>
internal sealed class SquashfsMetadataStream
{
    private const int MaximumWindows = 1 << 20;

    private readonly Stream _input;
    private readonly long _bytesUsed;
    private readonly bool _bigEndian;
    private readonly int _compression;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<long, byte[]> _windows = [];
    private readonly List<long> _chainOrder = [];
    private long _nextBlockRelative;
    private bool _endReached;

    /// <summary>
    /// Creates a window-chain reader rooted at an absolute image offset.
    /// <para>创建以绝对镜像偏移为根的窗口链读取器。</para>
    /// </summary>
    /// <param name="input">Seekable image stream; not closed here. <para>可定位的镜像流；本类不关闭它。</para></param>
    /// <param name="tableStart">Absolute start of the table's first metadata block. <para>表首个元数据块的绝对起始。</para></param>
    /// <param name="bytesUsed">Used image extent; every block read stays below it. <para>已使用的镜像范围；每次块读取都必须在其之下。</para></param>
    /// <param name="bigEndian">Header endianness taken from the superblock magic. <para>由超级块魔数决定的头字节序。</para></param>
    /// <param name="compression">Superblock compressor id used for encoded windows. <para>编码窗口所用的超级块压缩器标识。</para></param>
    /// <param name="cancellationToken">Token checked before every block read. <para>每次块读取前检查的令牌。</para></param>
    internal SquashfsMetadataStream(
        Stream input,
        long tableStart,
        long bytesUsed,
        bool bigEndian,
        int compression,
        CancellationToken cancellationToken)
    {
        _input = input;
        _bytesUsed = bytesUsed;
        _bigEndian = bigEndian;
        _compression = compression;
        _cancellationToken = cancellationToken;
        TableStart = tableStart;
    }

    /// <summary>Absolute image offset of the first window header. <para>首个窗口头的绝对镜像偏移。</para></summary>
    internal long TableStart { get; }

    /// <summary>
    /// Decodes the window stored at a table-relative block offset, walking the chain forward as needed.
    /// <para>解码表相对块偏移处的窗口，必要时沿链前推。</para>
    /// </summary>
    /// <param name="relativeBlock">The chain offset recorded in an inode address pair.
    /// <para>inode 地址对中记录的链偏移。</para></param>
    /// <returns>The decoded window bytes (at most 8 KiB). <para>解码后的窗口字节（至多 8 KiB）。</para></returns>
    /// <exception cref="InvalidDataException">The offset is past the chain end or a block is corrupt.
    /// <para>偏移越过链尾或块损坏时抛出。</para></exception>
    internal byte[] WindowAt(long relativeBlock)
    {
        if (_windows.TryGetValue(relativeBlock, out var cached))
        {
            return cached;
        }

        while (!_endReached && _nextBlockRelative <= relativeBlock)
        {
            ReadNextWindow();
            if (_windows.TryGetValue(relativeBlock, out var loaded))
            {
                return loaded;
            }
        }

        throw new InvalidDataException(
            $"The SquashFS metadata block {relativeBlock} at table {TableStart} lies beyond the window chain.");
    }

    /// <summary>Locates a window by its chain position, extending the walk when the index is not loaded yet.
    /// <para>按链位置定位窗口；若尚未加载则继续向前推进。</para></summary>
    /// <param name="relativeBlock">Table-relative chain offset. <para>表相对的链偏移。</para></param>
    /// <returns>The index into the chain order list. <para>链顺序列表中的下标。</para></returns>
    /// <exception cref="InvalidDataException">The offset is not part of this chain.
    /// <para>偏移不属于本链时抛出。</para></exception>
    internal int IndexOfBlock(long relativeBlock)
    {
        WindowAt(relativeBlock);
        var index = _chainOrder.IndexOf(relativeBlock);
        if (index < 0)
        {
            throw new InvalidDataException($"The SquashFS metadata block {relativeBlock} lies beyond the window chain.");
        }

        return index;
    }

    /// <summary>Reads the window at a chain index, decoding the next block when required.
    /// <para>读取指定链下标的窗口；必要时解码下一个块。</para></summary>
    /// <param name="index">Chain index; must be the loaded range or exactly one past it.
    /// <para>链下标；必须落在已加载范围或恰好为其末尾之后。</para></param>
    /// <returns>The decoded window bytes. <para>解码后的窗口字节。</para></returns>
    /// <exception cref="InvalidDataException">The chain ended before this index. <para>链在此下标之前已结束。</para></exception>
    internal byte[] WindowAtIndex(int index)
    {
        while (_chainOrder.Count <= index)
        {
            if (_endReached)
            {
                throw new InvalidDataException("The SquashFS metadata table has no further window.");
            }

            ReadNextWindow();
        }

        return _windows[_chainOrder[index]];
    }

    /// <summary>
    /// Creates a forward-only cursor over the table starting at a (block, offset) pair.
    /// <para>创建从 (块, 偏移) 对开始、仅向前的表游标。</para>
    /// </summary>
    /// <param name="relativeBlock">Chain offset of the starting window. <para>起始窗口的链偏移。</para></param>
    /// <param name="offset">Byte offset inside that window. <para>该窗口内的字节偏移。</para></param>
    /// <returns>A cursor yielding consecutive table bytes across window boundaries.
    /// <para>可跨窗口边界连续读取表字节的游标。</para></returns>
    internal Cursor CreateCursor(long relativeBlock, int offset) => new(this, relativeBlock, offset);

    private byte[] ReadNextWindow()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_chainOrder.Count >= MaximumWindows)
        {
            throw new InvalidDataException("The SquashFS metadata table exceeds the supported window count.");
        }

        var absolute = checked(TableStart + _nextBlockRelative);
        if (absolute + 2 > _bytesUsed)
        {
            throw new InvalidDataException($"The SquashFS metadata block header at {absolute} exceeds the image.");
        }

        Span<byte> header = stackalloc byte[2];
        _input.Position = absolute;
        _input.ReadExactly(header);
        var raw = _bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(header)
            : BinaryPrimitives.ReadUInt16LittleEndian(header);
        var stored = (raw & 0x8000) != 0;
        var size = raw & 0x7FFF;
        if (size == 0 || absolute + 2 + (long)size > _bytesUsed)
        {
            throw new InvalidDataException($"The SquashFS metadata block at {absolute} declares an out-of-range size {size}.");
        }

        var payload = new byte[size];
        _input.ReadExactly(payload);
        var decoded = stored
            ? payload
            : SquashfsDecompression.Decompress(_compression, payload, SquashfsSuperblock.MetadataWindowSize, exactOutput: false);
        if (decoded.Length is 0 or > SquashfsSuperblock.MetadataWindowSize)
        {
            throw new InvalidDataException($"The SquashFS metadata block at {absolute} expands to {decoded.Length} bytes.");
        }

        var relative = _nextBlockRelative;
        _windows[relative] = decoded;
        _chainOrder.Add(relative);
        _nextBlockRelative = checked(_nextBlockRelative + 2 + size);
        if (decoded.Length < SquashfsSuperblock.MetadataWindowSize)
        {
            _endReached = true;
        }

        return decoded;
    }

    /// <summary>Forward-only byte cursor over one metadata table, crossing window boundaries transparently.
    /// <para>单表仅向前的字节游标，透明跨越窗口边界。</para></summary>
    internal sealed class Cursor
    {
        private readonly SquashfsMetadataStream _table;
        private int _index;
        private byte[] _window;
        private int _position;

        internal Cursor(SquashfsMetadataStream table, long relativeBlock, int offset)
        {
            _table = table;
            _index = table.IndexOfBlock(relativeBlock);
            _window = table.WindowAtIndex(_index);
            if (offset < 0 || offset > _window.Length)
            {
                throw new InvalidDataException($"The SquashFS window offset {offset} exceeds the decoded window.");
            }

            _position = offset;
        }

        /// <summary>Reads exactly the requested bytes into a new array.
        /// <para>精确读取请求的字节数并返回新数组。</para></summary>
        /// <param name="count">Byte count to consume; may cross windows. <para>需消耗的字节数；可跨窗口。</para></param>
        /// <returns>The decoded table bytes. <para>解码后的表字节。</para></returns>
        /// <exception cref="InvalidDataException">The window chain ended before <paramref name="count"/> bytes.
        /// <para>窗口链在读满前结束时抛出。</para></exception>
        internal byte[] ReadBytes(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            var result = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                EnsureAvailable(1);
                var take = Math.Min(count - filled, _window.Length - _position);
                _window.AsSpan(_position, take).CopyTo(result.AsSpan(filled));
                _position += take;
                filled += take;
            }

            return result;
        }

        /// <summary>Skips forward through the table bytes.
        /// <para>在表字节中向前跳过。</para></summary>
        /// <param name="count">Byte count to skip; may cross windows. <para>需跳过的字节数；可跨窗口。</para></param>
        /// <exception cref="InvalidDataException">The window chain ended first. <para>窗口链提前结束。</para></exception>
        internal void Skip(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            while (count > 0)
            {
                EnsureAvailable(1);
                var take = Math.Min(count, _window.Length - _position);
                _position += take;
                count -= take;
            }
        }

        private void EnsureAvailable(int needed)
        {
            while (_window.Length - _position < needed)
            {
                if (_window.Length < SquashfsSuperblock.MetadataWindowSize)
                {
                    throw new InvalidDataException("The SquashFS metadata table ends before the requested span.");
                }

                _index++;
                _window = _table.WindowAtIndex(_index);
                _position = 0;
            }
        }
    }
}