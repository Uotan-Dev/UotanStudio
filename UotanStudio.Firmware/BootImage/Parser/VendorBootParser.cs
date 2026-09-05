using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Common.Streams;

namespace UotanStudio.Firmware.BootImage;

/// <summary>Parses Android vendor_boot version 3 and 4 images.</summary>
public class VendorBootParser : IDisposable
{
    private const string VendorBootMagic = "VNDRBOOT";
    private const int V3HeaderSize = 2112;
    private const int V4HeaderSize = 2128;
    private const int VendorCmdlineSize = 2048;
    private const int RamdiskNameSize = 32;
    private const int RamdiskBoardIdCount = 16;
    // AOSP vendor_boot v4 entry: size/offset/type (12) + name (32) + board_id[16] (64).
    private const int RamdiskTableEntryMinSize = 108;
    // The byte[] compatibility properties reject unreasonable single allocations. The streaming
    // extraction APIs below copy only what the caller explicitly requests, so they remain usable
    // within the range limits validated at parse time.
    private const uint MaximumComponentSize = 512 * 1024 * 1024;
    private const uint MaximumRamdiskTableEntries = 65_536;
    private const uint MaximumRamdiskTableEntrySize = 64 * 1024;
    private const uint MaximumRamdiskTableSize = 64 * 1024 * 1024;
    private bool _disposed;
    private readonly object _sourceLock = new();

    // Owned source stream is retained so components can be materialized lazily (byte[] properties,
    // small-component compatibility) or streamed out without ever building a full component array.
    // At parse time only metadata and file ranges are recorded; bytes are read on demand.
    private Stream? _sourceStream;
    private long _vendorRamdiskOffset;
    private long _dtbOffset;
    private long _bootconfigOffset;
    private byte[]? _vendorRamdisk;
    private byte[]? _dtb;
    private byte[]? _bootconfig;
    // Immutable parse snapshots keep lazy access safe even though Header remains a mutable
    // compatibility model exposed to callers.
    private uint _vendorRamdiskSize;
    private uint _dtbSize;
    private uint _bootconfigSize;

    public VendorBootHeader Header { get; private set; } = new();

    /// <summary>
    /// 获得 Vendor RAMDisk 的完整字节（小组件兼容入口；大组件请优先使用流式解包 API）。
    /// <para>Gets the full vendor ramdisk bytes (small-component compatibility entry; prefer the
    /// streaming extraction APIs for large components).</para>
    /// </summary>
    public byte[] VendorRamdisk
    {
        get
        {
            lock (_sourceLock)
            {
                ThrowIfDisposed();
                return _vendorRamdisk ??= ReadComponent(_vendorRamdiskOffset, _vendorRamdiskSize, "vendor ramdisk");
            }
        }
    }

    /// <summary>
    /// 获得 DTB 的完整字节（小组件兼容入口；大组件请优先使用流式解包 API）。
    /// <para>Gets the full DTB bytes (small-component compatibility entry; prefer the streaming
    /// extraction APIs for large components).</para>
    /// </summary>
    public byte[] Dtb
    {
        get
        {
            lock (_sourceLock)
            {
                ThrowIfDisposed();
                return _dtb ??= ReadComponent(_dtbOffset, _dtbSize, "DTB");
            }
        }
    }

    /// <summary>
    /// 获得 Bootconfig 的完整字节（仅 V4；小组件兼容入口）。
    /// <para>Gets the full bootconfig bytes (V4 only; small-component compatibility entry).</para>
    /// </summary>
    public byte[] Bootconfig
    {
        get
        {
            lock (_sourceLock)
            {
                ThrowIfDisposed();
                return _bootconfig ??= ReadComponent(_bootconfigOffset, _bootconfigSize, "bootconfig");
            }
        }
    }

    public List<VendorRamdiskTableEntry> RamdiskTable { get; } = [];

    public void ParseFile(string filePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        AttachSource(stream);
    }

    public void Parse(byte[] data)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);
        // Retain a parser-owned snapshot: lazy properties and streaming extraction must not observe
        // caller mutations made after Parse returns.
        var stream = new MemoryStream(data.ToArray(), writable: false);
        AttachSource(stream);
    }

    private void AttachSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_sourceLock)
        {
            try
            {
                ThrowIfDisposed();
                Reset();
                _sourceStream?.Dispose();
                _sourceStream = stream;
                ParseStream(stream);
            }
            catch
            {
                stream.Dispose();
                if (ReferenceEquals(_sourceStream, stream))
                {
                    _sourceStream = null;
                }
                // Parsing is transactional from the caller's perspective: neither partially parsed
                // metadata nor stale component ranges remain observable after a failure.
                Reset();
                throw;
            }
        }
    }

    private void ParseStream(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ImageParseException("Vendor Boot 输入流必须可读且可定位");
        }

        if (stream.Length < 12)
        {
            throw new ImageParseException($"文件太小，无法读取 Vendor Boot 基本头: {stream.Length} 字节");
        }

        var prefix = ReadRange(stream, 0, 12, "Vendor Boot 基本头");
        var magic = Encoding.ASCII.GetString(prefix, 0, 8);
        if (magic != VendorBootMagic)
        {
            throw new ImageParseException($"无效的 Vendor Boot 镜像 magic: {magic}");
        }

        Header.HeaderVersion = ReadUInt32LE(prefix, 8);
        var minimumHeaderSize = Header.HeaderVersion switch
        {
            3 => V3HeaderSize,
            4 => V4HeaderSize,
            _ => throw new ImageParseException($"不支持的 Vendor Boot 镜像版本: {Header.HeaderVersion}")
        };
        var header = ReadRange(stream, 0, minimumHeaderSize, $"V{Header.HeaderVersion} Vendor Boot 头部");
        ParseHeader(header, minimumHeaderSize);
        ExtractComponents(stream);
        if (Header.HeaderVersion == 4)
        {
            ExtractRamdiskTable(stream);
            ExtractBootconfig(stream);
        }
        if (Header.VendorRamdiskSize > 0)
        {
            Header.VendorRamdiskCompression = DetectCompression(stream, _vendorRamdiskOffset, Header.VendorRamdiskSize);
        }
    }

    private void ParseHeader(byte[] data, int minimumHeaderSize)
    {
        Header.PageSize = ReadUInt32LE(data, 12);
        Header.KernelLoadAddress = ReadUInt32LE(data, 16);
        Header.RamdiskLoadAddress = ReadUInt32LE(data, 20);
        Header.VendorRamdiskSize = ReadUInt32LE(data, 24);
        Header.VendorCmdline = ReadString(data, 28, VendorCmdlineSize).TrimEnd('\0');
        Header.TagsLoadAddress = ReadUInt32LE(data, 2076);
        Header.VendorHeaderSize = ReadUInt32LE(data, 2096);
        Header.DtbSize = ReadUInt32LE(data, 2100);
        Header.DtbAddress = ReadUInt64LE(data, 2104);

        if (Header.PageSize == 0 || (Header.PageSize & (Header.PageSize - 1)) != 0)
        {
            throw new ImageParseException($"无效的 Vendor Boot 页大小: {Header.PageSize}");
        }
        if (Header.VendorHeaderSize < minimumHeaderSize)
        {
            throw new ImageParseException($"Vendor Boot 头部大小过小: {Header.VendorHeaderSize}");
        }

        if (Header.HeaderVersion == 4)
        {
            Header.VendorRamdiskTableSize = ReadUInt32LE(data, 2112);
            Header.VendorRamdiskTableEntryNum = ReadUInt32LE(data, 2116);
            Header.VendorRamdiskTableEntrySize = ReadUInt32LE(data, 2120);
            Header.VendorBootconfigSize = ReadUInt32LE(data, 2124);
            ValidateTableFields();
        }

        _vendorRamdiskSize = Header.VendorRamdiskSize;
        _dtbSize = Header.DtbSize;
        _bootconfigSize = Header.VendorBootconfigSize;
    }

    private void ExtractComponents(Stream stream)
    {
        var ramdiskOffset = AlignToPage(Header.VendorHeaderSize, Header.PageSize);
        var dtbOffset = AlignToPage(CheckedAdd(ramdiskOffset, Header.VendorRamdiskSize), Header.PageSize);

        // 解析时只校验组件范围（拒绝截断或超大声明），不物化字节；字节在 byte[] 兼容属性或
        // 流式解包时按需读取。
        ValidateComponentRange(stream, ramdiskOffset, Header.VendorRamdiskSize, "vendor ramdisk");
        ValidateComponentRange(stream, dtbOffset, Header.DtbSize, "DTB");

        _vendorRamdiskOffset = ramdiskOffset;
        _dtbOffset = dtbOffset;

        if (Header.HeaderVersion == 4)
        {
            var tableOffset = AlignToPage(CheckedAdd(dtbOffset, Header.DtbSize), Header.PageSize);
            if (tableOffset > uint.MaxValue)
            {
                throw new ImageParseException("Vendor ramdisk 表偏移超出支持范围");
            }
            Header.VendorRamdiskTableOffset = (uint)tableOffset;
        }
    }

    private void ValidateTableFields()
    {
        if (Header.VendorRamdiskTableSize > MaximumRamdiskTableSize)
        {
            throw new ImageParseException($"Vendor ramdisk 表元数据超过上限: {Header.VendorRamdiskTableSize}");
        }
        if (Header.VendorRamdiskTableEntryNum == 0)
        {
            // AOSP v4 images may reserve a non-empty table region even when it has no entries.
            // Its declared size still participates in the following bootconfig layout.
            return;
        }
        if (Header.VendorRamdiskTableEntryNum > MaximumRamdiskTableEntries)
        {
            throw new ImageParseException($"Vendor ramdisk 表条目数超过上限: {Header.VendorRamdiskTableEntryNum}");
        }
        if (Header.VendorRamdiskTableEntrySize < RamdiskTableEntryMinSize ||
            Header.VendorRamdiskTableEntrySize > MaximumRamdiskTableEntrySize)
        {
            throw new ImageParseException($"Vendor ramdisk 表条目大小不合理: {Header.VendorRamdiskTableEntrySize}");
        }
        var required = checked((ulong)Header.VendorRamdiskTableEntryNum * Header.VendorRamdiskTableEntrySize);
        if (required > Header.VendorRamdiskTableSize)
        {
            throw new ImageParseException("Vendor ramdisk 表字段范围不一致");
        }
    }

    private void ExtractRamdiskTable(Stream stream)
    {
        // The complete declared table region determines the following component's location and
        // must exist, but padding beyond the entries is not parser input and must not be allocated.
        ValidateRange(stream, Header.VendorRamdiskTableOffset, Header.VendorRamdiskTableSize, "vendor ramdisk 表");
        if (Header.VendorRamdiskTableEntryNum == 0)
        {
            return;
        }

        for (uint i = 0; i < Header.VendorRamdiskTableEntryNum; i++)
        {
            var entryOffset = CheckedAdd(Header.VendorRamdiskTableOffset,
                checked((long)i * Header.VendorRamdiskTableEntrySize));
            // The declared stride/range was validated above, but only the standardized 108-byte
            // prefix is parser input. Ignore vendor extension/padding bytes without allocating them.
            var data = ReadRange(stream, entryOffset, RamdiskTableEntryMinSize, $"vendor ramdisk 表条目 {i}");
            var entry = new VendorRamdiskTableEntry
            {
                RamdiskSize = ReadUInt32LE(data, 0),
                RamdiskOffset = ReadUInt32LE(data, 4),
                RamdiskType = ReadUInt32LE(data, 8),
                RamdiskName = ReadString(data, 12, RamdiskNameSize).TrimEnd('\0')
            };
            if ((ulong)entry.RamdiskOffset + entry.RamdiskSize > _vendorRamdiskSize)
            {
                throw new ImageParseException($"Vendor ramdisk 表条目 {i} 超出 ramdisk 范围");
            }
            for (var j = 0; j < RamdiskBoardIdCount; j++)
            {
                entry.RamdiskBoardId[j] = ReadUInt32LE(data, 12 + RamdiskNameSize + (j * 4));
            }
            RamdiskTable.Add(entry);
        }
    }

    private void ExtractBootconfig(Stream stream)
    {
        if (Header.VendorBootconfigSize == 0)
        {
            return;
        }

        var offset = AlignToPage(
            CheckedAdd(Header.VendorRamdiskTableOffset, Header.VendorRamdiskTableSize), Header.PageSize);
        ValidateComponentRange(stream, offset, Header.VendorBootconfigSize, "bootconfig");
        _bootconfigOffset = offset;
    }

    /// <summary>
    /// 获得指定组件的声明长度（字节），不物化任何数据。
    /// <para>Gets the declared length in bytes of a component without materializing any data.</para>
    /// </summary>
    /// <param name="component">组件类型。<para>The component type.</para></param>
    public long GetComponentLength(VendorBootComponent component)
    {
        lock (_sourceLock)
        {
            EnsureParsed();
            return component switch
            {
                VendorBootComponent.VendorRamdisk => _vendorRamdiskSize,
                VendorBootComponent.Dtb => _dtbSize,
                VendorBootComponent.Bootconfig => _bootconfigSize,
                _ => throw new ArgumentOutOfRangeException(nameof(component))
            };
        }
    }

    /// <summary>
    /// 以有界只读流形式暴露指定组件（不复制组件数据；流的生命周期由调用方管理，底层源流由解析器持有）。
    /// <para>Exposes a component as a bounded read-only stream without copying its data. The caller
    /// manages the returned stream's lifetime; the underlying source stream is owned by the parser.</para>
    /// </summary>
    /// <param name="component">组件类型。<para>The component type.</para></param>
    /// <exception cref="InvalidOperationException">尚未解析镜像。<para>No image has been parsed.</para></exception>
    public Stream ExtractComponentStream(VendorBootComponent component)
    {
        lock (_sourceLock)
        {
            EnsureParsed();
            var (offset, size, name) = component switch
            {
                VendorBootComponent.VendorRamdisk => (_vendorRamdiskOffset, _vendorRamdiskSize, "vendor ramdisk"),
                VendorBootComponent.Dtb => (_dtbOffset, _dtbSize, "DTB"),
                VendorBootComponent.Bootconfig => (_bootconfigOffset, (long)_bootconfigSize, "bootconfig"),
                _ => throw new ArgumentOutOfRangeException(nameof(component))
            };
            var source = _sourceStream!;
            ValidateRange(source, offset, size, name);
            return new Common.Streams.BoundedReadStream(source, offset, size, leaveOpen: true, syncRoot: _sourceLock);
        }
    }

    /// <summary>
    /// 将指定组件精确复制到目标流（提前 EOF 抛 <see cref="EndOfStreamException"/>）。
    /// <para>Copies a component exactly to the destination stream (throws
    /// <see cref="EndOfStreamException"/> on premature EOF).</para>
    /// </summary>
    /// <param name="destination">可写目标流。<para>The writable destination stream.</para></param>
    /// <param name="component">组件类型。<para>The component type.</para></param>
    public void ExtractComponentToFile(Stream destination, VendorBootComponent component)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var source = ExtractComponentStream(component);
        StreamCopy.CopyExactly(source, destination, source.Length);
    }

    /// <summary>
    /// 将指定组件流式写入文件（创建或覆盖 <paramref name="destinationPath"/>）。
    /// <para>Streams a component to a file, creating or overwriting
    /// <paramref name="destinationPath"/>.</para>
    /// </summary>
    /// <param name="destinationPath">目标文件路径。<para>The destination file path.</para></param>
    /// <param name="component">组件类型。<para>The component type.</para></param>
    public void ExtractComponentToFile(string destinationPath, VendorBootComponent component)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var source = ExtractComponentStream(component);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        StreamCopy.CopyExactly(source, destination, source.Length);
    }

    /// <summary>
    /// 以有界只读流形式暴露 ramdisk 表中的某个条目（偏移相对于 Vendor RAMDisk 起始处）。
    /// <para>Exposes one ramdisk table entry as a bounded read-only stream (the entry offset is
    /// relative to the start of the vendor ramdisk).</para>
    /// </summary>
    /// <param name="entry">ramdisk 表条目。<para>The ramdisk table entry.</para></param>
    public Stream ExtractRamdiskStream(VendorRamdiskTableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sourceLock)
        {
            EnsureParsed();
            if (entry.RamdiskOffset > _vendorRamdiskSize ||
                entry.RamdiskSize > _vendorRamdiskSize - entry.RamdiskOffset)
            {
                throw new ImageParseException("Vendor ramdisk 表条目超出 ramdisk 范围");
            }
            long offset;
            try
            {
                offset = checked(_vendorRamdiskOffset + entry.RamdiskOffset);
            }
            catch (OverflowException ex)
            {
                throw new ImageParseException("Vendor ramdisk 表条目偏移溢出", ex);
            }
            var size = (long)entry.RamdiskSize;
            var source = _sourceStream!;
            ValidateRange(source, offset, size, "vendor ramdisk 表条目");
            return new Common.Streams.BoundedReadStream(source, offset, size, leaveOpen: true, syncRoot: _sourceLock);
        }
    }

    /// <summary>
    /// 将 ramdisk 表中的某个条目流式写入文件（创建或覆盖 <paramref name="destinationPath"/>）。
    /// <para>Streams one ramdisk table entry to a file, creating or overwriting
    /// <paramref name="destinationPath"/>.</para>
    /// </summary>
    /// <param name="destinationPath">目标文件路径。<para>The destination file path.</para></param>
    /// <param name="entry">ramdisk 表条目。<para>The ramdisk table entry.</para></param>
    public void ExtractRamdiskToFile(string destinationPath, VendorRamdiskTableEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var source = ExtractRamdiskStream(entry);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        StreamCopy.CopyExactly(source, destination, source.Length);
    }

    private byte[] ReadComponent(long offset, uint size, string component)
    {
        if (_sourceStream is null || size == 0)
        {
            return [];
        }
        return ReadRange(_sourceStream, offset, size, component);
    }

    private void EnsureParsed()
    {
        ThrowIfDisposed();
        if (_sourceStream is null)
        {
            throw new InvalidOperationException("尚未解析任何 Vendor Boot 镜像");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static CompressionType DetectCompression(Stream stream, long offset, uint size)
    {
        var length = (int)Math.Min(size, 8U);
        // BootCompressor.Detect only needs the leading magic bytes; a tiny prefix avoids
        // materializing the whole vendor ramdisk just to detect its compression format.
        var prefix = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(prefix);
        return BootCompressor.Detect(prefix);
    }

    private static void ValidateComponentRange(Stream stream, long offset, uint size, string component)
    {
        // Parse-time validation is range-only. The 512 MiB allocation guard belongs exclusively to
        // byte[] compatibility accessors; bounded streaming extraction supports larger components.
        ValidateRange(stream, offset, size, component);
    }

    private static byte[] ReadRange(Stream stream, long offset, uint size, string component)
    {
        if (size > MaximumComponentSize)
        {
            throw new ImageParseException($"{component} 超过单组件上限 {MaximumComponentSize} 字节，无法安全读取");
        }
        return ReadRange(stream, offset, checked((int)size), component);
    }

    private static byte[] ReadRange(Stream stream, long offset, int size, string component)
    {
        ValidateRange(stream, offset, size, component);
        var position = checked(offset);
        var result = new byte[size];
        stream.Position = position;
        stream.ReadExactly(result);
        return result;
    }

    private static void ValidateRange(Stream stream, long offset, long size, string component)
    {
        if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
        {
            throw new ImageParseException($"{component} 范围超出或文件被截断");
        }
    }

    private static uint ReadUInt32LE(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static ulong ReadUInt64LE(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
    private static string ReadString(byte[] data, int offset, int length) =>
        Encoding.ASCII.GetString(data, offset, length);

    private static long CheckedAdd(long left, long right)
    {
        try { return checked(left + right); }
        catch (OverflowException ex) { throw new ImageParseException("Vendor Boot 组件偏移溢出", ex); }
    }

    private static long AlignToPage(long offset, uint pageSize)
    {
        var mask = (long)pageSize - 1;
        return CheckedAdd(offset, mask) & ~mask;
    }

    private void Reset()
    {
        Header = new VendorBootHeader();
        _vendorRamdiskOffset = 0;
        _dtbOffset = 0;
        _bootconfigOffset = 0;
        _vendorRamdisk = null;
        _dtb = null;
        _bootconfig = null;
        _vendorRamdiskSize = 0;
        _dtbSize = 0;
        _bootconfigSize = 0;
        RamdiskTable.Clear();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        lock (_sourceLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _sourceStream?.Dispose();
                _sourceStream = null;
                Reset();
            }
        }
    }

    // LockedBoundedStream 已迁移至 Common/Streams/BoundedReadStream（syncRoot 参数）。
    // LockedBoundedStream has been migrated to Common/Streams/BoundedReadStream (syncRoot parameter).
}

/// <summary>
/// Vendor Boot 镜像的组件类型。
/// <para>Component types of a vendor boot image.</para>
/// </summary>
public enum VendorBootComponent
{
    /// <summary>
    /// Vendor RAMDisk（可能为压缩的 CPIO 归档）。
    /// <para>Vendor ramdisk (possibly a compressed CPIO archive).</para>
    /// </summary>
    VendorRamdisk,

    /// <summary>
    /// 设备树 Blob（DTB）。
    /// <para>Device Tree Blob (DTB).</para>
    /// </summary>
    Dtb,

    /// <summary>
    /// Vendor Bootconfig（仅 V4）。
    /// <para>Vendor bootconfig (V4 only).</para>
    /// </summary>
    Bootconfig
}