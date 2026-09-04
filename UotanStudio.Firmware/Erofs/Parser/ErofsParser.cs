using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 文件清单条目（路径 + 大小 + nid + 元数据）。
/// <para>An erofs file-list entry (path + size + nid + metadata).</para>
/// </summary>
public sealed record ErofsFileEntry(
    string Path,
    long Size,
    ulong Nid)
{
    /// <summary>文件模式（含类型位，如 0x81A4）。File mode including the type bits.</summary>
    public ushort Mode { get; init; }

    /// <summary>uid。User id.</summary>
    public uint Uid { get; init; }

    /// <summary>gid。Group id.</summary>
    public uint Gid { get; init; }

    /// <summary>修改时间（Unix 秒）。Modification time in Unix seconds.</summary>
    public ulong MTime { get; init; }

    /// <summary>是否符号链接。Whether this entry is a symbolic link.</summary>
    public bool IsSymlink => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeSymlink;

    /// <summary>是否目录。Whether this entry is a directory.</summary>
    public bool IsDirectory => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeDirectory;

    /// <summary>是否字符设备。Whether this entry is a character device.</summary>
    public bool IsCharacterDevice => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeCharacterDevice;

    /// <summary>是否块设备。Whether this entry is a block device.</summary>
    public bool IsBlockDevice => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeBlockDevice;

    /// <summary>是否命名管道。Whether this entry is a named pipe (FIFO).</summary>
    public bool IsFifo => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeFifo;

    /// <summary>是否套接字。Whether this entry is a socket.</summary>
    public bool IsSocket => (Mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeSocket;

    /// <summary>是否特殊文件（字符/块设备、FIFO、套接字；无数据内容）。
    /// Whether this entry is a special file (char/block device, FIFO, socket; no data content).</summary>
    public bool IsSpecialFile => IsCharacterDevice || IsBlockDevice || IsFifo || IsSocket;

    /// <summary>符号链接目标（仅 IsSymlink 时有值）。Symbolic link target (only set for symlinks).</summary>
    public string? SymlinkTarget { get; init; }

    /// <summary>该文件的 xattr 列表（名称已含前缀解析）。Xattrs of this file (names prefix-resolved).</summary>
    public IReadOnlyList<ErofsXattrEntry>? Xattrs { get; init; }
}

/// <summary>
/// 流式 erofs 镜像解析器：持有可 seek 的数据源（FileStream、外部传入的 Stream，或对内存 byte[] 的轻量包装），
/// 按需读取超级块 / inode / 目录块 / 压缩索引 / pcluster，不再整镜像载入内存。
/// 数据布局与解压语义严格对齐 Linux 内核主线 fs/erofs/{zmap,dir,data}.c：
/// 支持 flat / inline（tailpacking）/ compressed-full / compressed-compact / chunk-based 布局，
/// LZ4 / LZMA / DEFLATE / ZSTD 四种算法，以及 dense 目录、共享 xattr 与 fragment 引用。
/// 未知 incompat 特性位在构造时显式拒绝；INTERLACED_PCLUSTER（仅 PLAIN 簇，读端旋转还原）
/// 已支持；METABOX 特性（incompat 0x100）在构造时读取 metabox inode 数据并缓存，
/// ReadInode 遇带 bit63 的 nid 时从缓存的 metabox 数据中按槽位索引 * 32 解析紧凑 inode。
/// 多设备支持：构造时可传入多设备流列表，解析设备表，chunk 数据按 device_id 路由到对应流。
/// <para>A streaming erofs image parser: holds a seekable source (a FileStream, an externally supplied
/// Stream, or a lightweight wrapper over in-memory bytes) and reads the superblock, inodes, directory
/// blocks, compressed indexes and pclusters on demand instead of loading the whole image into memory.
/// Data mapping and decompression semantics strictly follow the mainline Linux kernel
/// fs/erofs/{zmap,dir,data}.c: flat, inline (tailpacking), compressed-full, compressed-compact and
/// chunk-based layouts, LZ4 / LZMA / DEFLATE / ZSTD algorithms, plus dense directories, shared
/// xattrs and fragment references. Unknown incompat feature bits are rejected explicitly at
/// construction; INTERLACED_PCLUSTER (PLAIN clusters only, rotated back on read) is supported;
/// the METABOX feature (incompat 0x100) reads and caches the metabox inode data at construction
/// time, and ReadInode resolves bit-63 nids from the cached metabox data by slot-index * 32.
/// Multi-device support: a constructor accepts a list of device streams, parses the device table,
/// and routes chunk data reads to the correct device stream by device_id.</para>
/// </summary>
public sealed class ErofsParser : IDisposable
{
    private const int CopyBufferSize = 1 << 20;

    /// <summary>单次载入内存的元数据组件（目录数据 / 压缩索引表 / 共享 xattr）大小上限，防止损坏镜像造成过量分配。
    /// Upper bound for one in-memory metadata component (directory data / compressed index table /
    /// shared xattr area).</summary>
    private const long MaxMetadataComponentSize = 512L * 1024 * 1024;

    /// <summary>dirent nid 的 METABOX 标记位（<c>EROFS_DIRENT_NID_METABOX_BIT = 63</c>）：
    /// 置位表示低 63 位是 METABOX inode 数据流内的槽位索引，而非经典 nid。
    /// METABOX marker bit of a dirent nid (<c>EROFS_DIRENT_NID_METABOX_BIT = 63</c>): when set, the
    /// low 63 bits are a slot index inside the METABOX inode data stream rather than a classic nid.</summary>
    private const ulong NidMetaboxFlag = 1UL << 63;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _baseOffset;
    private readonly long _imageSize;
    private readonly object _streamLock = new();
    private readonly ErofsSuperblock _superblock;
    private readonly int _blockSize;
    private readonly bool _hasMetabox;
    private readonly byte[]? _metaboxData;
    private readonly Stream?[]? _deviceStreams;
    private readonly ErofsDeviceSlot[]? _deviceSlots;
    private List<ErofsFileEntry>? _fileTable;
    private ErofsInode? _packedInode;

    /// <summary>
    /// 从镜像字节创建解析器（字节数组直接包装为内存流，不复制；校验偏移 1024 处的魔数）。
    /// <para>Creates a parser from image bytes (the array is wrapped in a memory stream without
    /// copying; validates the magic at offset 1024).</para>
    /// </summary>
    public ErofsParser(byte[] image)
        : this(CreateMemorySource(image), ownsStream: false)
    {
    }

    /// <summary>
    /// 从流创建解析器。流可 seek 时按需读取；否则回退为读入内存缓冲（保持向后兼容）。
    /// 解析器不取得流的所有权，调用方负责释放。
    /// <para>Creates a parser from a stream. A seekable stream is read on demand; a non-seekable one
    /// falls back to buffering into memory (backward compatible). The parser does not take ownership;
    /// the caller remains responsible for disposing the stream.</para>
    /// </summary>
    public ErofsParser(Stream stream)
        : this(stream, ownsStream: false)
    {
    }

    /// <summary>
    /// 从文件路径创建解析器（内部打开并持有 FileStream；释放解析器即关闭文件）。
    /// <para>Creates a parser from a file path (opens and owns a FileStream; disposing the parser
    /// closes the file).</para>
    /// </summary>
    public ErofsParser(string filePath)
        : this(OpenFileSource(filePath), ownsStream: true)
    {
    }

    public ErofsParser(IReadOnlyList<Stream> deviceStreams)
        : this(ValidateDeviceStreams(deviceStreams)[0], ownsStream: false)
    {
        if (deviceStreams.Count < 2) return;
        var streams = ValidateDeviceStreams(deviceStreams);
        if (_deviceStreams is not null)
        {
            for (var i = 1; i < streams.Length && i < _deviceStreams.Length; i++)
                _deviceStreams[i] = streams[i];
        }
        else
        {
            _deviceStreams = new Stream[streams.Length];
            for (var i = 0; i < streams.Length; i++)
                _deviceStreams[i] = streams[i];
        }
    }

    private static Stream[] ValidateDeviceStreams(IReadOnlyList<Stream> deviceStreams)
    {
        ArgumentNullException.ThrowIfNull(deviceStreams);
        if (deviceStreams.Count == 0)
            throw new ArgumentException("At least one device stream is required.", nameof(deviceStreams));
        var streams = new Stream[deviceStreams.Count];
        for (var i = 0; i < deviceStreams.Count; i++)
            streams[i] = deviceStreams[i] ?? throw new ArgumentNullException(nameof(deviceStreams), $"Device stream at index {i} is null.");
        return streams;
    }

    private ErofsParser(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Stream? sourceToDisposeOnFailure = ownsStream ? stream : null;
        try
        {
            if (!stream.CanRead)
            {
                throw new ArgumentException("The source stream must be readable.", nameof(stream));
            }

            if (stream.CanSeek)
            {
                try
                {
                    _baseOffset = stream.Position;
                    var sourceLength = stream.Length;
                    if (_baseOffset < 0 || sourceLength < _baseOffset)
                    {
                        throw new InvalidDataException("The source stream position must be within its length.");
                    }

                    _imageSize = sourceLength - _baseOffset;
                    _stream = stream;
                    _ownsStream = ownsStream;
                }
                catch (NotSupportedException)
                {
                    var staged = StageToTemporaryFile(stream);
                    if (ownsStream)
                    {
                        stream.Dispose();
                    }

                    _stream = staged;
                    sourceToDisposeOnFailure = _stream;
                    _ownsStream = true;
                    _baseOffset = 0;
                    _imageSize = _stream.Length;
                }
            }
            else
            {
                _stream = StageToTemporaryFile(stream);
                sourceToDisposeOnFailure = _stream;
                _ownsStream = true;
                _baseOffset = 0;
                _imageSize = _stream.Length;
            }

            if (_imageSize < ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize)
            {
                throw new FormatException($"Image too small to hold an EROFS superblock: {_imageSize} bytes.");
            }

            // 超级块槽位 = 128 + sb_extslots * 16 字节；METABOX 的 s_metabox_nid 位于扩展槽
            //（superblock 偏移 128，镜像字节 1152..1160）。先读 128 字节主槽位解析出
            // sb_extslots，再按需扩读完整槽位，使 ErofsSuperblock.FromBytes 能取到扩展字段。
            // The superblock slot is 128 + sb_extslots * 16 bytes; the METABOX s_metabox_nid lives
            // in the extended slot (superblock offset 128, image bytes 1152..1160). Read the 128-byte
            // main slot to learn sb_extslots, then re-read the full slot so FromBytes can reach the
            // extended fields.
            var sbCore = ReadBytes(ErofsSuperblock.SuperblockOffset, ErofsSuperblock.SuperblockSize, "Superblock");
            var preliminarySuperblock = ErofsSuperblock.FromBytes(sbCore);
            var sbSlotSize = ErofsSuperblock.SuperblockSize
                             + preliminarySuperblock.SbExtSlots * ErofsSuperblock.ExtSlotSize;
            _superblock = sbSlotSize > ErofsSuperblock.SuperblockSize
                ? ErofsSuperblock.FromBytes(ReadBytes(
                    ErofsSuperblock.SuperblockOffset,
                    checked((int)Math.Min(sbSlotSize, _imageSize - ErofsSuperblock.SuperblockOffset)),
                    "Superblock"))
                : preliminarySuperblock;
            // The kernel only accepts blkszbits in [9, PAGE_SHIFT] (512..65536 bytes).
            // 内核仅接受 blkszbits ∈ [9, PAGE_SHIFT]（512..65536 字节）。
            if (_superblock.BlockSizeBits is < 9 or > 16)
            {
                throw new FormatException(
                    $"Unsupported EROFS block size shift {_superblock.BlockSizeBits} (expected 9..16).");
            }

            // 未知的不兼容特性位必须显式拒绝：内核同样拒绝无法理解的 incompat 位，静默忽略
            // 会把镜像按错误布局解析（如未来新增位改变 inode 表或数据布局）。已知位并集
            // 见 ErofsSuperblock.AllIncompat（含 LZ4_0PADDING/BIG_PCLUSTER/CHUNKED_FILE/
            // DEVICE_TABLE/ZTAILPACKING/FRAGMENTS/XATTR_PREFIXES/48BIT/METABOX）。
            // Unknown incompatible feature bits must be rejected explicitly: the kernel rejects
            // incompat bits it cannot understand as well, and silently ignoring them would parse
            // the image with the wrong layout (a future bit may change the inode table or data
            // layout). The known-bit union is ErofsSuperblock.AllIncompat (LZ4_0PADDING/
            // BIG_PCLUSTER/CHUNKED_FILE/DEVICE_TABLE/ZTAILPACKING/FRAGMENTS/XATTR_PREFIXES/
            // 48BIT/METABOX).
            var unknownIncompat = _superblock.FeatureIncompat & ~ErofsSuperblock.AllIncompat;
            if (unknownIncompat != 0)
            {
                throw new FormatException(
                    $"EROFS image uses unknown incompatible feature bits 0x{unknownIncompat:X8}.");
            }

            _hasMetabox = _superblock.HasMetabox;
            _blockSize = _superblock.BlockSize;

            // METABOX 初始化：读取 metabox inode 的全部数据并缓存，供 ReadInode 解析 bit63 nid。
            // METABOX init: read the full metabox inode data and cache it for bit-63 nid resolution.
            if (_hasMetabox && _superblock.MetaBoxNid != 0)
            {
                _metaboxData = ReadMetaboxData(_superblock.MetaBoxNid);
            }

            // 多设备表初始化：当 s_extra_devices > 0 且 DEVICE_TABLE 特性开启时，
            // 在 s_devt_slotoff * blockSize 处解析设备表。
            // Multi-device table init: parse the device table when s_extra_devices > 0 and the
            // DEVICE_TABLE feature is set.
            if (_superblock.HasDeviceTable && _superblock.ExtraDevices > 0)
            {
                _deviceStreams = new Stream[_superblock.ExtraDevices + 1];
                _deviceStreams[0] = _stream;
                _deviceSlots = new ErofsDeviceSlot[_superblock.ExtraDevices + 1];
                // 主设备槽位（设备 0）：块数为镜像总块数，映射偏移为 0。
                // Primary device slot (device 0): total image blocks, mapping offset 0.
                _deviceSlots[0] = new ErofsDeviceSlot { Blocks = (uint)_superblock.BlockCount64, MappedBlkAddr = 0 };
                var devTableOffset = (long)_superblock.DevtSlotoff * _blockSize;
                for (var i = 0; i < _superblock.ExtraDevices; i++)
                {
                    var slotOffset = devTableOffset + (long)i * ErofsDeviceSlot.Size;
                    var slotData = ReadBytes(slotOffset, ErofsDeviceSlot.Size, $"Device slot {i + 1}");
                    _deviceSlots[i + 1] = ErofsDeviceSlot.FromBytes(slotData);
                }
            }
        }
        catch
        {
            sourceToDisposeOnFailure?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 判断镜像字节是否包含 erofs 魔数。
    /// <para>Checks whether the image bytes hold the erofs magic.</para>
    /// </summary>
    public static bool CanHandle(byte[] image) => image is not null && ErofsSuperblock.HasMagic(image);

    /// <summary>
    /// 块大小（字节）。
    /// <para>Block size in bytes.</para>
    /// </summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// 文件系统总块数（s_blocks）。
    /// <para>Total filesystem block count (s_blocks).</para>
    /// </summary>
    public long BlockCount => (long)_superblock.BlockCount64;

    /// <summary>
    /// 镜像总字节数（分区大小）。
    /// <para>Total image size in bytes (partition size).</para>
    /// </summary>
    public long ImageSize => _imageSize;

    /// <summary>
    /// inode 总数（s_inos）。
    /// <para>Total inode count (s_inos).</para>
    /// </summary>
    public long InodeCount => (long)_superblock.Inos;

    /// <summary>
    /// 根目录 nid（s_root_nid；48 位布局下为 s_rootnid_8b）。
    /// <para>Root directory nid (s_root_nid; s_rootnid_8b under the 48-bit layout).</para>
    /// </summary>
    public ulong RootNid => _superblock.RootNid64;

    /// <summary>
    /// 卷名（s_volume_name）。
    /// <para>Volume name (s_volume_name).</para>
    /// </summary>
    public string VolumeName => _superblock.VolumeName;

    /// <summary>
    /// 超级块（供上层读取特性位等）。
    /// <para>The superblock (for callers reading feature flags etc.).</para>
    /// </summary>
    internal ErofsSuperblock Superblock => _superblock;

    /// <summary>
    /// 遍历目录树并返回文件清单（路径使用 '/' 分隔）。
    /// <para>Walks the directory tree and returns the file listing (paths use '/').</para>
    /// </summary>
    public IReadOnlyList<ErofsFileEntry> EnumerateFiles()
    {
        _fileTable ??= EnumerateFilesCore();
        return _fileTable;
    }

    /// <summary>
    /// 读取单个文件内容并返回字节数组。
    /// <para>Reads a single file and returns its bytes.</para>
    /// </summary>
    public byte[] ReadFileBytes(string path)
    {
        using var buffer = new MemoryStream();
        ReadFile(path, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 读取单个文件内容到输出流（plain 直读，inline 直读，compressed 逐 pcluster 解压，chunk 逐块拼接）。
    /// <para>Reads a single file into the output stream (plain data read directly, inline data read
    /// directly, compressed data decompressed pcluster by pcluster, chunk-based data assembled chunk
    /// by chunk).</para>
    /// </summary>
    public void ReadFile(string path, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(outputStream);

        var nid = FindFile(path);
        var inode = ReadInode(nid);
        WriteFileData(nid, inode, outputStream);
    }

    /// <summary>
    /// 判定 inode 是否为无数据内容的特殊文件（字符/块设备、FIFO、套接字）。
    /// <para>Whether the inode is a special file with no data content (char/block device, FIFO, socket).</para>
    /// </summary>
    private static bool IsSpecialFile(ErofsInode inode)
        => inode.IsCharacterDevice || inode.IsBlockDevice || inode.IsFifo || inode.IsSocket;

    /// <summary>
    /// 将镜像中全部文件解包到输出目录。每个文件先写入同目录临时文件，完整后再原子替换为目标文件；
    /// 中途失败不会留下半成品文件。
    /// <para>Extracts all files to the output directory. Each file is first written to a temporary file
    /// in the same directory and atomically moved into place only once complete; a failure never
    /// leaves a partial output file behind.</para>
    /// </summary>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        foreach (var entry in EnumerateFiles())
        {
            var target = Path.Combine(outputDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!overwrite && File.Exists(target))
            {
                continue;
            }

            if (entry.IsSymlink)
            {
                // 符号链接写入为文本目标文件（Windows 无可靠符号链接语义，写入目标路径文本）。
                // Symlinks are written as a text file holding the target (Windows has no reliable
                // symlink semantics; write the target path as text).
                File.WriteAllText(target, entry.SymlinkTarget ?? string.Empty);
                continue;
            }

            ExtractFileAtomic(target, entry.Path, overwrite);
        }
    }

    /// <summary>
    /// 释放解析器持有的源（仅当解析器自己打开的文件流）。
    /// <para>Disposes the source held by the parser (only when the parser opened the file itself).</para>
    /// </summary>
    public void Dispose()
    {
        if (_deviceStreams is not null)
        {
            for (var i = 1; i < _deviceStreams.Length; i++)
            {
                _deviceStreams[i]?.Dispose();
            }
        }

        if (_ownsStream)
        {
            _stream.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static Stream CreateMemorySource(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new MemoryStream(image, writable: false);
    }

    private static FileStream OpenFileSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("EROFS image file not found.", filePath);
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static FileStream StageToTemporaryFile(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-erofs-parser-{Guid.NewGuid():N}.tmp");
        FileStream? staging = null;
        try
        {
            staging = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferSize,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            stream.CopyTo(staging, CopyBufferSize);
            staging.Position = 0;
            return staging;
        }
        catch
        {
            staging?.Dispose();
            try { File.Delete(path); } catch { /* Best-effort cleanup must not hide the primary failure. */ }
            throw;
        }
    }

    /// <summary>
    /// 从数据源读取 <paramref name="length"/> 字节；先做范围校验（offset &lt;= len 且 size &lt;= len - offset），
    /// 再按 ReadExactly 精确读取。
    /// <para>Reads <paramref name="length"/> bytes from the source; validates the range first
    /// (offset &lt;= len and size &lt;= len - offset), then reads exactly via ReadExactly.</para>
    /// </summary>
    private byte[] ReadMetaboxData(ulong metaboxNid)
    {
        var metaboxOffset = _superblock.GetInodeOffset(metaboxNid);
        if (metaboxOffset < 0 || metaboxOffset > _imageSize - 2)
            throw new InvalidDataException($"METABOX inode nid {metaboxNid} is out of range.");

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(
            ReadBytes(metaboxOffset, 2, "METABOX inode advise"));
        var inodeSize = (advise & ErofsInode.FlagExtended) != 0 ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
        if (metaboxOffset > _imageSize - inodeSize)
            throw new InvalidDataException("METABOX inode exceeds the image bounds.");

        var inode = ErofsInode.FromBytes(ReadBytes(metaboxOffset, inodeSize, "METABOX inode"));
        var dataSize = (long)inode.Size;
        if (dataSize <= 0) return [];
        if (dataSize > MaxMetadataComponentSize)
            throw new InvalidDataException($"METABOX inode data size {dataSize} exceeds the limit of {MaxMetadataComponentSize}.");

        using var buffer = new MemoryStream();
        switch (inode.DataLayout)
        {
            case ErofsInode.DataLayoutInline:
                {
                    var inlineStart = metaboxOffset + inode.InlineDataOffset;
                    CheckRange(inlineStart, dataSize, "METABOX inline data");
                    CopyFromSource(buffer, inlineStart, dataSize);
                    break;
                }
            case ErofsInode.DataLayoutFlat:
                {
                    var start = checked(inode.RawBlockAddress * _blockSize);
                    CheckRange(start, dataSize, "METABOX flat data");
                    CopyFromSource(buffer, start, dataSize);
                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported METABOX inode data layout {inode.DataLayout}.");
        }

        return buffer.ToArray();
    }

    private byte[] ReadDeviceBytes(int deviceId, long offset, int length, string what)
    {
        if (_deviceStreams is null || deviceId >= _deviceStreams.Length || _deviceStreams[deviceId] is null)
            return ReadBytes(offset, length, what);

        if (offset < 0 || length < 0)
            throw new InvalidDataException($"{what} has invalid offset/length.");

        var buffer = new byte[length];
        lock (_streamLock)
        {
            _deviceStreams[deviceId]!.Position = offset;
            _deviceStreams[deviceId]!.ReadExactly(buffer);
        }

        return buffer;
    }

    private byte[] ReadBytes(long offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
        {
            throw new InvalidDataException($"{what} exceeds the image bounds.");
        }

        var buffer = new byte[length];
        lock (_streamLock)
        {
            _stream.Position = checked(_baseOffset + offset);
            _stream.ReadExactly(buffer);
        }

        return buffer;
    }

    /// <summary>
    /// 将数据源中 [offset, offset + length) 的字节直接拷贝到输出流（分块，不整体载入）。
    /// <para>Copies [offset, offset + length) bytes from the source to the output stream in chunks
    /// without loading the whole range into memory.</para>
    /// </summary>
    private void CopyFromSource(Stream output, long offset, long length)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            lock (_streamLock)
            {
                _stream.Position = checked(_baseOffset + offset);
                while (length > 0)
                {
                    var count = (int)Math.Min(CopyBufferSize, length);
                    _stream.ReadExactly(buffer, 0, count);
                    output.Write(buffer, 0, count);
                    length -= count;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ExtractFileAtomic(string target, string entryPath, bool overwrite)
    {
        var tempPath = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ReadFile(entryPath, output);
                output.Flush(flushToDisk: true);
            }

            File.Move(tempPath, target, overwrite);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 尽力清理临时文件。Best-effort cleanup of the temporary file.
            }

            throw;
        }
    }

    private List<ErofsFileEntry> EnumerateFilesCore()
    {
        var result = new List<ErofsFileEntry>();
        var visitedDirectories = new HashSet<ulong>();
        WalkDirectory(_superblock.RootNid64, string.Empty, visitedDirectories, result);
        return result;
    }

    private void WalkDirectory(ulong nid, string prefix, HashSet<ulong> visitedDirectories, List<ErofsFileEntry> result)
    {
        if (!visitedDirectories.Add(nid))
        {
            // 已访问的目录（如 `..` 反向引用祖先）直接跳过，不视为错误。
            // Already-visited directories (e.g. `..` back-references to ancestors) are skipped
            // rather than treated as an error.
            return;
        }

        var inode = ReadInode(nid);
        if (!inode.IsDirectory)
        {
            return;
        }

        foreach (var entry in ReadDirectory(nid, inode))
        {
            if (entry.FileType == ErofsInode.FileTypeDirectory)
            {
                // EROFS 目录条目可能含 `.`/`..` 自引用（nid 指向当前目录自身或其父目录），
                // 跳过自引用以避免重复遍历；父目录引用已被 visitedDirectories 去重。
                // EROFS directory entries may contain `.`/`..` self-references (nid pointing back to
                // this directory or its parent); skip self-references to avoid re-traversal. Parent
                // references are de-duplicated by visitedDirectories.
                if (entry.Nid == nid)
                {
                    continue;
                }

                var directoryPath = JoinPath(prefix, entry.Name);
                WalkDirectory(entry.Nid, directoryPath, visitedDirectories, result);
                continue;
            }

            if (entry.FileType == ErofsInode.FileTypeRegularFile ||
                entry.FileType == ErofsInode.FileTypeSymlink ||
                entry.FileType is ErofsInode.FileTypeCharacterDevice or
                    ErofsInode.FileTypeBlockDevice or ErofsInode.FileTypeFifo or
                    ErofsInode.FileTypeSocket)
            {
                var fileInode = ReadInode(entry.Nid);
                var path = JoinPath(prefix, entry.Name);
                var fileEntry = new ErofsFileEntry(path, (long)fileInode.Size, entry.Nid)
                {
                    Mode = fileInode.Mode,
                    Uid = fileInode.Uid,
                    Gid = fileInode.Gid,
                    MTime = fileInode.CTime,
                };
                if (fileInode.IsSymlink)
                {
                    // 符号链接：目标即文件内容（inline 或 flat）。
                    // Symlink: the target is the file content (inline or flat).
                    try
                    {
                        using var buffer = new MemoryStream();
                        WriteFileData(entry.Nid, fileInode, buffer);
                        fileEntry = fileEntry with { SymlinkTarget = ReadSymlinkTarget(buffer) };
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException)
                    {
                        fileEntry = fileEntry with { SymlinkTarget = null };
                    }
                }
                else if (fileInode.IsCharacterDevice || fileInode.IsBlockDevice ||
                         fileInode.IsFifo || fileInode.IsSocket)
                {
                    // 特殊文件（设备/管道/套接字）无数据内容；不读取（其 i_u 存设备号）。
                    // Special files (devices/pipes/sockets) carry no data; skip reading
                    // (their i_u field holds the device number).
                    result.Add(fileEntry);
                    continue;
                }

                var xattrs = ReadXattrs(entry.Nid, fileInode);
                if (xattrs is { Count: > 0 })
                {
                    fileEntry = fileEntry with { Xattrs = xattrs };
                }

                result.Add(fileEntry);
            }
        }
    }

    private static string? ReadSymlinkTarget(Stream content)
    {
        content.Position = 0;
        using var reader = new StreamReader(content, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var target = reader.ReadToEnd();
        return string.IsNullOrEmpty(target) ? null : target;
    }

    private static string JoinPath(string prefix, string name)
        => prefix.Length == 0 ? name : prefix + "/" + name;

    /// <summary>
    /// 读取 inode 的 xattr 列表：解析 inline xattr ibody（含共享 xattr id 引用解析）。
    /// 解析失败或损坏时返回空列表（不阻断文件列举）。
    /// <para>Reads the inode's xattr list: parses the inline xattr ibody (including shared xattr id
    /// resolution). Returns an empty list on parse failure or corruption (never blocks enumeration).</para>
    /// </summary>
    private List<ErofsXattrEntry> ReadXattrs(ulong nid, ErofsInode inode)
    {
        var result = new List<ErofsXattrEntry>();
        if (inode.XattrICount == 0)
        {
            return result;
        }

        try
        {
            // inline xattr ibody 紧随 inode 之后（ibody 大小 = XattrTotalSize）。
            // The inline xattr ibody follows the inode (ibody size = XattrTotalSize).
            var ibodyOffset = _superblock.GetInodeOffset(nid) + inode.OnDiskSize;
            if (ibodyOffset < 0 || ibodyOffset > _imageSize - ErofsXattr.HeaderSize)
            {
                return result;
            }

            var ibody = ReadBytes(ibodyOffset, inode.XattrTotalSize, "xattr ibody");
            var prefixTable = LoadXattrPrefixTable();
            var entries = ErofsXattr.ParseInline(ibody, inode.XattrICount, prefixTable);
            result.AddRange(entries);

            // 共享 xattr 条目由 ibody 头部的 h_shared_count 个 u32 id 引用。
            // Shared xattr entries are referenced by h_shared_count u32 ids in the ibody header.
            var sharedIds = ErofsXattr.ParseSharedXattrIds(ibody, inode.XattrICount);
            if (sharedIds.Length > 0 && _superblock.XattrBlkAddr != 0)
            {
                // 共享 xattr 区自 xattr_blkaddr 块起始；条目跨块时按需读取整个共享区。
                // The shared xattr area starts at the xattr_blkaddr block; read the whole area on
                // demand as entries may span blocks.
                var sharedStart = _superblock.XattrBlkAddr * _blockSize;
                var sharedLength = Math.Min(_imageSize - sharedStart, MaxMetadataComponentSize);
                if (sharedLength > 0)
                {
                    var sharedArea = ReadBytes(sharedStart, checked((int)sharedLength), "shared xattr area");
                    foreach (var id in sharedIds)
                    {
                        if (ErofsXattr.TryParseSharedEntry(sharedArea, id, prefixTable, out var sharedEntry))
                        {
                            result.Add(sharedEntry);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            // xattr 解析失败不影响文件提取；忽略并返回已收集的条目。
            // A failed xattr parse must not break file extraction; ignore and return what we have.
        }

        return result;
    }

    /// <summary>
    /// 加载长 xattr 名称前缀表（feature XATTR_PREFIXES）；无特性时返回 null。
    /// <para>Loads the long xattr name prefix table (feature XATTR_PREFIXES); null without the
    /// feature.</para>
    /// </summary>
    private IReadOnlyList<(byte BaseIndex, string Infix)>? LoadXattrPrefixTable()
    {
        if (!_superblock.HasXattrPrefixes || _superblock.XattrPrefixCount == 0)
        {
            return null;
        }

        var tableOffset = ErofsXattr.GetPrefixTableByteOffset(_superblock.XattrPrefixStart);
        if (tableOffset < 0 || tableOffset > _imageSize)
        {
            return null;
        }

        var tableLength = Math.Min(_imageSize - tableOffset, MaxMetadataComponentSize);
        var table = ReadBytes(tableOffset, checked((int)tableLength), "xattr prefix table");
        return ErofsXattr.ParsePrefixTable(table, _superblock.XattrPrefixCount);
    }

    private ulong FindFile(string path)
    {
        foreach (var entry in EnumerateFiles())
        {
            if (string.Equals(entry.Path, path, StringComparison.Ordinal))
            {
                return entry.Nid;
            }
        }

        throw new FileNotFoundException($"File not found in erofs image: '{path}'.", path);
    }

    private ErofsInode ReadInode(ulong nid)
    {
        // METABOX 特性（incompat 0x100）下，dirent 的 nid 可能带 bit63 标记：低 63 位不是经典
        // inode 表槽号，而是 metabox inode 数据流（metabox 容器）内的紧凑 inode 槽位索引。
        // 本解析器把 metabox inode 的全部数据缓存于 _metaboxData，此处按 slot × 32 定位 32 字节
        // compact 记录。仅支持“纯 32 字节记录”；记录内联携带 xattr/尾部数据/索引表的 metabox
        // inode 无法读回（写端 ErofsBuilder 也只把纯 flat 的普通文件放入 metabox 容器）。
        // With the METABOX feature (incompat 0x100) a dirent nid may carry bit 63: the low 63 bits
        // are then not a classic inode-table slot but a compact-inode slot index inside the metabox
        // inode data stream (the metabox container). The parser caches the full metabox inode data in
        // _metaboxData and resolves such nids as slot × 32-byte compact records. Only pure 32-byte
        // records are supported; a metabox inode record carrying inline xattrs/tails/index tables is
        // not readable here (ErofsBuilder likewise only places plain flat regular files in the
        // metabox container).
        if (_hasMetabox && (nid & NidMetaboxFlag) != 0)
        {
            if (_metaboxData is null || _metaboxData.Length == 0)
                throw new InvalidDataException(
                    $"Inode nid 0x{nid:X16} references the METABOX data stream, but no METABOX data was cached.");

            var slotIndex = nid & ~NidMetaboxFlag;
            var slotOffset = checked((long)slotIndex * ErofsSuperblock.CompactInodeSize);
            if (slotOffset < 0 || slotOffset + ErofsSuperblock.CompactInodeSize > _metaboxData.Length)
                throw new InvalidDataException(
                    $"METABOX slot index {slotIndex} (offset {slotOffset}) is out of range (metabox data size {_metaboxData.Length}).");

            return ErofsInode.FromBytes(
                _metaboxData.AsSpan((int)slotOffset, ErofsSuperblock.CompactInodeSize));
        }

        var offset = _superblock.GetInodeOffset(nid);
        if (offset < 0 || offset > _imageSize - 2)
        {
            throw new InvalidDataException($"Inode nid {nid} is out of range.");
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(
            ReadBytes(offset, 2, $"Inode nid {nid}"));
        var inodeSize = (advise & ErofsInode.FlagExtended) != 0 ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
        if (offset < 0 || inodeSize < 0 || offset > _imageSize || inodeSize > _imageSize - offset)
        {
            throw new InvalidDataException($"Inode nid {nid} exceeds the image bounds.");
        }

        return ErofsInode.FromBytes(ReadBytes(offset, inodeSize, $"Inode nid {nid}"));
    }

    /// <summary>
    /// 目录块大小（superblock 的 s_dirblkbits 为 0 时等于块大小）。
    /// <para>Directory block size (equal to the block size when s_dirblkbits is 0).</para>
    /// </summary>
    private int DirectoryBlockSize => _superblock.DirBlkBits == 0 ? _blockSize : 1 << _superblock.DirBlkBits;

    private List<ErofsDirectoryEntry> ReadDirectory(ulong nid, ErofsInode inode)
    {
        var dirBlockSize = DirectoryBlockSize;
        if (inode.DataLayout == ErofsInode.DataLayoutInline)
        {
            // inline 布局：目录数据紧随 inode 与 inline xattr 之后，位于同一元数据块内。
            // Inline layout: the directory data follows the inode and inline xattrs within the same metadata block.
            var start = _superblock.GetInodeOffset(nid) + inode.InlineDataOffset;
            var length = (long)inode.Size;
            CheckRange(start, length, "Inline directory data");
            var data = ReadBytes(start, checked((int)Math.Min(length, MaxMetadataComponentSize)), "Inline directory data");
            return ParseDirectoryData(data, dirBlockSize, length);
        }

        if (inode.DataLayout != ErofsInode.DataLayoutFlat)
        {
            // 压缩布局的目录不存在（目录数据恒为 flat/inline）。
            // Directories never use compressed layouts (directory data is always flat/inline).
            throw new InvalidDataException($"Unsupported directory data layout {inode.DataLayout} for nid {nid}.");
        }

        // 扁平布局：i_u 为数据起始块号。
        // Flat layout: i_u is the start block of the data.
        var flatStart = checked(inode.RawBlockAddress * _blockSize);
        var flatLength = (long)inode.Size;
        CheckRange(flatStart, flatLength, "Directory data");
        if (flatLength > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Directory data size {flatLength} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        // 逐目录块按需读取，单块内存占用及目录总长度均有明确上限。
        // Read directory blocks on demand while bounding both per-block memory and total input.
        var result = new List<ErofsDirectoryEntry>();
        for (var done = 0L; done < flatLength; done += dirBlockSize)
        {
            var blockLength = (int)Math.Min(dirBlockSize, flatLength - done);
            var block = ReadBytes(flatStart + done, blockLength, "Directory data");
            result.AddRange(ParseDirectoryData(block, dirBlockSize, blockLength));
        }

        return result;
    }

    /// <summary>
    /// 解析单块目录数据：优先按内核 <c>erofs_fill_dentries</c> 语义解析 compact dirent；
    /// 若首条目 nameoff 带 dense 高位标记（0x8000）则回退到 dense 解析（长名头槽位）。
    /// <para>Parses one directory block: compact dirents per the kernel <c>erofs_fill_dentries</c>
    /// semantics first; falls back to dense parsing (long-name head slots) when the first entry's
    /// nameoff carries the dense high-bit flag (0x8000).</para>
    /// </summary>
    private static List<ErofsDirectoryEntry> ParseDirectoryData(ReadOnlySpan<byte> block, int blockSize, long totalLength)
    {
        if (block.Length >= ErofsDirectoryEntry.DirentSize)
        {
            var firstNameOff = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
            if ((firstNameOff & ErofsDirectoryEntry.NameOffHeadFlag) != 0)
            {
                return ErofsDirectoryEntry.ParseDenseBlock(block, blockSize);
            }
        }

        return ErofsDirectoryEntry.ParseBlock(block);
    }

    private void CheckRange(long offset, long length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
        {
            throw new InvalidDataException($"{what} exceeds the image bounds.");
        }
    }

    private void WriteFileData(ulong nid, ErofsInode inode, Stream output)
    {
        if (IsSpecialFile(inode))
        {
            // 特殊文件（字符/块设备、FIFO、套接字）无数据内容：i_u 存设备号而非块地址，
            // 按数据布局读取会得到错误字节。显式失败而非静默产出伪数据。
            // Special files (char/block devices, FIFOs, sockets) carry no data: i_u holds the
            // device number instead of a block address, so reading by data layout would yield
            // garbage. Fail loudly instead of silently producing bogus bytes.
            throw new InvalidDataException(
                $"Inode nid {nid} is a special file (mode 0x{inode.Mode:X4}) with no data content.");
        }

        switch (inode.DataLayout)
        {
            case ErofsInode.DataLayoutInline:
                // inline 布局（EROFS_INODE_FLAT_INLINE）：文件数据紧随 inode 与 inline xattr 之后，
                // 位于同一元数据块内；i_u 为哨兵 0xffffffff 时表示整个文件都是 inline 数据。
                // Inline layout (EROFS_INODE_FLAT_INLINE): the file data follows the inode and inline
                // xattrs within the same metadata block; when i_u is the sentinel 0xffffffff the whole
                // file is inline data.
                WriteInlineData(nid, inode, output);
                break;
            case ErofsInode.DataLayoutFlat:
                WriteFlatData(inode, output);
                break;
            case ErofsInode.DataLayoutChunkBased:
                WriteChunkData(nid, inode, output);
                break;
            case ErofsInode.DataLayoutCompressed:
            case ErofsInode.DataLayoutCompactIndexes:
                WriteCompressedData(nid, inode, output);
                break;
            default:
                // Fail loudly instead of silently reading raw bytes as file content.
                // 显式失败而非静默把原始字节当作文件内容。
                throw new InvalidDataException($"Unsupported EROFS data layout {inode.DataLayout} for inode nid {nid}.");
        }
    }

    private void WriteInlineData(ulong nid, ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        // FLAT_INLINE = tail-packing：文件主体为 flat 数据（自 i_u 块起连续），仅最后一个
        // 不足一块的尾部内联在 inode + xattr 之后（内核 data.c __erofs_map_blocks：
        // pos = (ceil(size/blksz) - 1) * blksz；m_la < pos 时 m_pa = i_u*blksz + m_la，
        // 否则从 iloc + isize + xattr_isize 读取内联尾部）。mkfs 的 flat 块数 =
        // floor(size/blksz)（size 恰为整块时无内联尾）。
        // FLAT_INLINE means tail-packing: the bulk of the file is flat data (contiguous from the
        // i_u block) and only the trailing part (less than one block) is inlined right after the
        // inode + xattrs. mkfs stores floor(size/blksz) flat blocks (no inline tail when size is an
        // exact multiple of the block size).
        var flatLength = (long)(inode.Size / (ulong)_blockSize * (ulong)_blockSize);
        if (flatLength > 0)
        {
            var flatStart = checked(inode.RawBlockAddress * _blockSize);
            CheckRange(flatStart, flatLength, "Tailpacked flat file data");
            CopyFromSource(output, flatStart, flatLength);
        }

        var tailLength = length - flatLength;
        if (tailLength > 0)
        {
            var tailStart = _superblock.GetInodeOffset(nid) + inode.InlineDataOffset;
            CheckRange(tailStart, tailLength, "Tailpacked inline data");
            CopyFromSource(output, tailStart, tailLength);
        }
    }

    private void WriteFlatData(ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        var start = checked(inode.RawBlockAddress * _blockSize);
        CheckRange(start, length, "File data");
        CopyFromSource(output, start, length);
    }

    /// <summary>
    /// 读取 chunk-based 文件（EROFS_INODE_CHUNK_BASED）：按 chunk 索引表逐块拼接数据。
    /// chunk 索引（8 字节 erofs_inode_chunk_index：startblk_hi u16 + device_id u16 + startblk_lo u32）
    /// 位于 inode + xattr 之后；chunk 大小 = 块大小 &lt;&lt; chunk blkbits（chunk format 低 5 位）。
    /// EROFS_NULL_ADDR（0xFFFFFFFFFFFFFFFF）表示空洞（零填充）。
    /// <para>Reads a chunk-based file (EROFS_INODE_CHUNK_BASED): assembles data chunk by chunk from
    /// the chunk index table. Chunk indexes (8-byte erofs_inode_chunk_index: startblk_hi u16 +
    /// device_id u16 + startblk_lo u32) sit right after the inode + xattrs; the chunk size equals
    /// block size &lt;&lt; chunk blkbits (low 5 bits of the chunk format). EROFS_NULL_ADDR
    /// (0xFFFFFFFFFFFFFFFF) marks a hole (zero fill).</para>
    /// </summary>
    private void WriteChunkData(ulong nid, ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        var chunkBlkBits = inode.ChunkFormatBlkBits;
        var chunkSize = _blockSize << chunkBlkBits;
        if (chunkSize <= 0)
        {
            throw new InvalidDataException($"Invalid chunk size (blkbits {chunkBlkBits}) for nid {nid}.");
        }

        var chunkCount = (length + chunkSize - 1) / chunkSize;
        var usesIndexes = inode.UsesChunkIndexes;
        var indexTableOffset = _superblock.GetInodeOffset(nid) + inode.InlineDataOffset;
        var indexBytes = usesIndexes
            ? checked(chunkCount * 8L)
            : checked(chunkCount * 4L);
        if (indexBytes > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Chunk index table size {indexBytes} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        var index = ReadBytes(indexTableOffset, checked((int)indexBytes), "Chunk index table");
        var remaining = length;
        for (var i = 0; i < chunkCount && remaining > 0; i++)
        {
            var chunkLen = (int)Math.Min(chunkSize, remaining);
            ulong startBlk;
            var deviceId = 0;
            if (usesIndexes)
            {
                var slot = index.AsSpan(i * 8, 8);
                startBlk = ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(slot) << 32)
                           | BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(4));
                deviceId = BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(2));
            }
            else
            {
                // 4 字节块号数组（EROFS_BLOCK_MAP_ENTRY_SIZE）：块号按顺序拼接。
                // 4-byte block-number array: block numbers concatenated in order.
                startBlk = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 4, 4));
            }

            if (startBlk == ErofsNullAddr)
            {
                // 空洞：零填充。Hole: zero fill.
                var zeroes = new byte[Math.Min(chunkLen, 1 << 16)];
                var zeroed = 0;
                while (zeroed < chunkLen)
                {
                    var n = Math.Min(zeroes.Length, chunkLen - zeroed);
                    output.Write(zeroes, 0, n);
                    zeroed += n;
                }

                remaining -= chunkLen;
                continue;
            }

            if (deviceId > 0 && _deviceStreams is not null && deviceId < _deviceStreams.Length)
            {
                var devOffset = checked((long)startBlk * _blockSize);
                var devData = ReadDeviceBytes(deviceId, devOffset, chunkLen, "Multi-device chunk data");
                output.Write(devData, 0, devData.Length);
            }
            else
            {
                var dataStart = checked((long)startBlk * _blockSize);
                CheckRange(dataStart, chunkLen, "Chunk data");
                CopyFromSource(output, dataStart, chunkLen);
            }
            remaining -= chunkLen;
        }
    }

    /// <summary>空洞哨兵块号（EROFS_NULL_ADDR = -1）。Hole sentinel block number (EROFS_NULL_ADDR = -1).</summary>
    private const ulong ErofsNullAddr = ulong.MaxValue;

    // ---- 压缩数据读取（内核 fs/erofs/zmap.c 语义）。Compressed data reading (kernel zmap.c). ----

    private const byte LclusterTypePlain = 0;
    private const byte LclusterTypeHead1 = 1;
    private const byte LclusterTypeNonHead = 2;
    private const byte LclusterTypeHead2 = 3;

    /// <summary>z_erofs_map_header 之后、紧凑索引起始前的固定字节数（内核 Z_EROFS_MAP_HEADER_END 之后 8 字节）。
    /// Fixed bytes between z_erofs_map_header and the full index start (8 bytes after Z_EROFS_MAP_HEADER_END).</summary>
    private const int FullIndexStartPad = 8;

    /// <summary>
    /// 读取压缩文件（COMPRESSED_FULL / COMPRESSED_COMPACT）。严格对照内核
    /// <c>z_erofs_fill_inode</c> + <c>z_erofs_map_blocks_fo</c> + <c>z_erofs_load_lcluster_from_disk</c>：
    /// 读取 8 字节 z_erofs_map_header 确定算法与簇大小，按紧凑（4B/2B 位级）或完整（8B）索引逐
    /// pcluster 解压，支持 big pcluster（D0_CBLKCNT）、PLAIN 字面簇、tailpacking（INLINE_PCLUSTER）
    /// 与 fragment 引用。
    /// <para>Reads a compressed file (COMPRESSED_FULL / COMPRESSED_COMPACT), strictly following the
    /// kernel <c>z_erofs_fill_inode</c> + <c>z_erofs_map_blocks_fo</c> +
    /// <c>z_erofs_load_lcluster_from_disk</c>: reads the 8-byte z_erofs_map_header to determine the
    /// algorithm and cluster size, then decompresses pcluster by pcluster from compact (bit-level
    /// 4B/2B) or full (8B) indexes, supporting big pclusters (D0_CBLKCNT), PLAIN literal clusters,
    /// tailpacking (INLINE_PCLUSTER) and fragment references.</para>
    /// </summary>
    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output)
    {
        var inodeLoc = _superblock.GetInodeOffset(nid);
        // 压缩配置头 z_erofs_map_header（8 字节）位于 inode + xattr 之后、8 字节对齐处。
        // The z_erofs_map_header (8 bytes) sits right after the inode + xattrs, aligned to 8 bytes.
        var headerOffset = AlignUp(inodeLoc + inode.OnDiskSize + inode.XattrTotalSize, 8);
        var headerBytes = ReadBytes(headerOffset, ZErofsMapHeader.Size, "compressed map header");
        var header = ZErofsMapHeader.FromBytes(headerBytes);

        // fragment-inode：整文件打包在 packed inode 中（h_clusterbits bit7）。
        // Fragment inode: the whole file is packed into the packed inode (h_clusterbits bit 7).
        if (header.IsFragmentInode)
        {
            var fragmentOff = header.FragmentOff & ~(1u << ZErofsMapHeader.FragmentInodeBit);
            WritePackedData(inode, fragmentOff, output);
            return;
        }

        var advise = header.Advise;
        var algorithmHead1 = header.Algorithm;
        // HEAD2 算法编码于 h_algorithmtype 高 4 位（内核 z_erofs_fill_inode）。
        // The HEAD2 algorithm is encoded in the high nibble of h_algorithmtype (kernel z_erofs_fill_inode).
        var algorithmHead2 = (ErofsAlgorithm)((header.AlgorithmType >> 4) & ZErofsMapHeader.AlgorithmTypeMask);
        var lclusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        if (lclusterBits > 14)
        {
            throw new InvalidDataException($"Logical cluster size shift {lclusterBits} exceeds the limit of 14.");
        }

        var lclusterSize = 1 << lclusterBits;
        var totalIdx = ErofsCompression.PclusterCount(inode.Size, lclusterSize);
        if (totalIdx == 0)
        {
            return;
        }

        var bigPcluster1 = (advise & ZErofsMapHeader.AdviseBigPcluster1) != 0;
        var bigPcluster2 = (advise & ZErofsMapHeader.AdviseBigPcluster2) != 0;
        var tailPacking = (advise & ZErofsMapHeader.AdviseInlinePcluster) != 0;
        var fragmentPcluster = (advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0;
        // 交错 pcluster（Z_EROFS_ADVISE_INTERLACED_PCLUSTER）：仅对 PLAIN 字面簇生效——mkfs
        // 把相邻 pcluster 的数据交错填入同一物理块以消除块间空洞，读端需整块读入后按块内
        // 偏移旋转还原（内核 z_erofs_map_blocks_fo：PLAIN 簇 + 该 advise → m_algorithmformat =
        // INTERLACED；z_erofs_decompress 的 INTERLACED 分支做旋转复制）。
        // Interlaced pclusters (Z_EROFS_ADVISE_INTERLACED_PCLUSTER) affect only PLAIN literal
        // clusters: mkfs interleaves neighbouring pclusters into one physical block to remove
        // intra-block holes, so the reader must load the whole block and rotate it back by the
        // in-block offset (kernel z_erofs_map_blocks_fo: PLAIN + this advise →
        // m_algorithmformat = INTERLACED; z_erofs_decompress handles it with a rotated copy).
        var interlaced = (advise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0;
        if (bigPcluster1 != bigPcluster2)
        {
            // 内核要求 HEAD1/HEAD2 big-pcluster 标志一致；不一致视为损坏。
            // The kernel requires the HEAD1/HEAD2 big-pcluster advises to be consistent.
            throw new InvalidDataException($"Inconsistent big-pcluster advises 0x{advise:X4} for nid {nid}.");
        }

        var compressed = ArrayPool<byte>.Shared.Rent(_blockSize * 2);
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(lclusterSize, _blockSize));
        try
        {
            long lcn = 0;
            while (lcn < totalIdx)
            {
                var lc = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn, out var ebase, out var compacted4bInitial, out var compacted2b);
                if (lc.Type == LclusterTypeNonHead)
                {
                    // NONHEAD 簇属于其前一个 HEAD pcluster，随 HEAD 一起解压；跳过。
                    // This cluster belongs to the previous HEAD pcluster and is covered by its
                    // decompressed data; skip it.
                    lcn++;
                    continue;
                }

                // HEAD/PLAIN 簇：逻辑起点 mLa = (lcn << lclusterbits) | clusterofs。
                // HEAD/PLAIN cluster: logical start mLa = (lcn << lclusterbits) | clusterofs.
                var mLa = lcn * lclusterSize + lc.ClusterOfs;
                if (mLa >= (long)inode.Size)
                {
                    lcn++;
                    continue;
                }

                // 向后找下一个 HEAD（跳过 NONHEAD）以确定本 pcluster 的解压长度边界。
                // Scan forward past NONHEAD clusters to find the next HEAD, bounding this pcluster.
                var nextLcn = lcn + 1;
                long nextMLa;
                while (true)
                {
                    if (nextLcn >= totalIdx)
                    {
                        nextMLa = (long)inode.Size;
                        break;
                    }

                    var t2 = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, nextLcn, out _, out _, out _);
                    if (t2.Type != LclusterTypeNonHead)
                    {
                        nextMLa = nextLcn * lclusterSize + t2.ClusterOfs;
                        break;
                    }

                    nextLcn++;
                }

                var llen = Math.Min(nextMLa, (long)inode.Size) - mLa;
                if (llen <= 0)
                {
                    lcn = nextLcn;
                    continue;
                }

                if (llen > MaxMetadataComponentSize)
                {
                    throw new InvalidDataException(
                        $"Pcluster decompressed size {llen} exceeds the limit of {MaxMetadataComponentSize} bytes.");
                }

                // 尾部打包（tailpacking）：最后一个 pcluster 的数据内联在 map header 之后。
                // Tail packing: the last pcluster's data is inlined right after the map header.
                var isTailPacked = tailPacking && nextLcn >= totalIdx;
                if (isTailPacked)
                {
                    var idataSize = (int)header.IdataSize;
                    if (idataSize < 0 || idataSize > llen)
                    {
                        throw new InvalidDataException($"Invalid tailpacking idata size {idataSize} for nid {nid}.");
                    }

                    if (idataSize == 0)
                    {
                        // 无内联数据时按字面簇处理（空洞？零填充）。No inline data: treat as a hole.
                        WriteZeroes(output, llen);
                        lcn = nextLcn;
                        continue;
                    }

                    var tailStart = headerOffset + ZErofsMapHeader.Size;
                    CheckRange(tailStart, idataSize, "Tailpacked data");
                    CopyFromSource(output, tailStart, idataSize);
                    // 尾部打包的剩余部分为逻辑空洞（零填充）。The rest of a tailpacked pcluster is a hole.
                    if (llen > idataSize)
                    {
                        WriteZeroes(output, llen - idataSize);
                    }

                    lcn = nextLcn;
                    continue;
                }

                if (lc.Type == LclusterTypePlain)
                {
                    // 字面簇：数据未压缩。普通情形（SHIFTED）物理上从 pblk 块起始连续存放；
                    // clusterofs 是逻辑簇内偏移，只用于 m_la 计算，不叠加到物理位置（内核
                    // z_erofs_map_blocks_fo 的 m_pa = erofs_pos(sb, m.pblk)，随后按 SHIFTED
                    // 直接复制 m_llen 字节）。
                    // Literal cluster: the data is stored uncompressed. In the normal (SHIFTED)
                    // case it is contiguous from the pblk block start; clusterofs is the logical
                    // in-cluster offset used only for m_la, never added to the physical position
                    // (kernel z_erofs_map_blocks_fo sets m_pa = erofs_pos(sb, m.pblk) then copies
                    // m_llen bytes as SHIFTED).
                    var dataStart = checked(lc.Pblk * _blockSize);
                    if (!interlaced)
                    {
                        CheckRange(dataStart, llen, "Plain lcluster data");
                        CopyFromSource(output, dataStart, llen);
                        lcn = nextLcn;
                        continue;
                    }

                    // 交错 pcluster（INTERLACED）：mkfs 把相邻 pcluster 交错填入同一物理块，
                    // 字面数据在该块内从 skip = blkoff(m_la) 处起连续存放并绕回块首。读端
                    // 整块读入后旋转还原（对照 erofs-utils z_erofs_decompress 的 INTERLACED
                    // 分支：rightpart = min(blksz - skip, count)，先取 in[skip..]，再取块首
                    // 剩余）。该特性仅由 mkfs 交错写端产生，真实镜像罕见；约束不符即显式失败。
                    // Interlaced pcluster (INTERLACED): mkfs interleaves neighbouring pclusters
                    // into one physical block, so the literal data starts at skip = blkoff(m_la)
                    // inside the block and wraps around the block head. The reader loads the whole
                    // block and rotates it back (cf. z_erofs_decompress INTERLACED branch:
                    // rightpart = min(blksz - skip, count), take in[skip..] first then the block
                    // head remainder). Only the mkfs interleaved writer produces this; real images
                    // rarely do. Constraints that do not hold fail loudly.
                    if (llen > _blockSize || llen < 0)
                    {
                        throw new InvalidDataException(
                            $"Interlaced plain pcluster {lcn} of nid {nid} has length {llen} > block size {_blockSize}.");
                    }

                    var skip = (int)(mLa & (_blockSize - 1));
                    CheckRange(dataStart, _blockSize, "Interlaced plain lcluster block");
                    var rawBlock = ReadBytes(dataStart, _blockSize, "Interlaced plain lcluster block");
                    var rightpart = Math.Min(_blockSize - skip, (int)llen);
                    output.Write(rawBlock, skip, rightpart);
                    if (llen > rightpart)
                    {
                        output.Write(rawBlock, 0, (int)llen - rightpart);
                    }

                    lcn = nextLcn;
                    continue;
                }

                // HEAD1/HEAD2 压缩簇：确定压缩块数并解压。
                // HEAD1/HEAD2 compressed cluster: determine the compressed block count and decompress.
                var compressedBlocks = 1;
                var headType = lc.Type;
                var head1NotBig = headType == LclusterTypeHead1 && !bigPcluster1;
                var plainOrHead2NotBig = (headType == LclusterTypePlain || headType == LclusterTypeHead2) && !bigPcluster2;
                var atEof = (lcn << lclusterBits) >= (long)inode.Size;
                if (!head1NotBig && !plainOrHead2NotBig && !atEof)
                {
                    // 需要从下一个 NONHEAD 的 D0_CBLKCNT 取得压缩块数（big pcluster）。
                    // The compressed block count comes from the next NONHEAD's D0_CBLKCNT (big pcluster).
                    var next = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn + 1, out _, out _, out _);
                    if (next.Type != LclusterTypeNonHead)
                    {
                        // 下一个条目是 PLAIN/HEAD 而非 CBLKCNT：单块 pcluster。
                        // The next entry is PLAIN/HEAD rather than CBLKCNT: a one-block pcluster.
                        compressedBlocks = 1;
                    }
                    else
                    {
                        // 内核要求首个 NONHEAD 的 delta[0] == 1（带 CBLKCNT 的条目在解码时
                        // 已把 delta[0] 归一化为 1 并将压缩块数放入 CompressedBlocks）；delta[0]
                        // 非 1 才是损坏。对照 erofs-utils z_erofs_get_extent_compressedlen：
                        // "if (m->delta[0] != 1) return -EFSCORRUPTED"。
                        // The kernel requires delta[0] == 1 on the first NONHEAD (a CBLKCNT entry
                        // is normalised to delta[0] == 1 at decode time with the block count in
                        // CompressedBlocks); anything but 1 is corrupt. Cf. erofs-utils
                        // z_erofs_get_extent_compressedlen: "if (m->delta[0] != 1) ... corrupt".
                        if (next.Delta0 != 1 || next.CompressedBlocks == 0)
                        {
                            throw new InvalidDataException(
                                $"Bogus CBLKCNT @ lcn {lcn + 1} of nid {nid}.");
                        }

                        compressedBlocks = next.CompressedBlocks;
                    }
                }

                var compressedByteLen = checked(compressedBlocks * _blockSize);
                if (compressedByteLen <= 0 || lc.Pblk < 0)
                {
                    throw new InvalidDataException("Invalid compressed pcluster for nid {nid}.");
                }

                var pblkStart = checked(lc.Pblk * _blockSize);
                CheckRange(pblkStart, compressedByteLen, "Compressed pcluster data");
                if (compressedByteLen > compressed.Length)
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                    compressed = ArrayPool<byte>.Shared.Rent(compressedByteLen);
                }

                if (llen > decoded.Length)
                {
                    ArrayPool<byte>.Shared.Return(decoded);
                    decoded = ArrayPool<byte>.Shared.Rent((int)llen);
                }

                lock (_streamLock)
                {
                    _stream.Position = checked(_baseOffset + pblkStart);
                    _stream.ReadExactly(compressed, 0, compressedByteLen);
                }

                // LZ4_0PADDING / 压缩配置特性：压缩数据前可能存在 0 前缀（mkfs 写入时
                // 从 dst-padding 起写），erofs-utils 解压前经 z_erofs_fixup_insize 跳过前导 0。
                // Compressed data may carry a leading-zero prefix (0-padding feature: mkfs writes
                // from dst - padding); erofs-utils skips it via z_erofs_fixup_insize before
                // decompression.
                var inputMargin = 0;
                while (inputMargin < compressedByteLen && compressed[inputMargin] == 0)
                {
                    inputMargin++;
                }

                if (inputMargin >= compressedByteLen)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} of nid {nid} is entirely zero-padded (no compressed data).");
                }

                var algorithm = headType == LclusterTypeHead2 ? algorithmHead2 : algorithmHead1;
                int decodedLen;
                try
                {
                    decodedLen = ErofsCompression.AlgorithmDecompress(
                        algorithm, compressed, inputMargin, compressedByteLen - inputMargin, decoded, 0, (int)llen);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} of nid {nid} failed to decompress ({algorithm}): {ex.Message}");
                }

                if (decodedLen != (int)llen)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} decoded to {decodedLen} bytes, expected {llen}.");
                }

                output.Write(decoded, 0, (int)llen);
                lcn = nextLcn;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }

    private static void WriteZeroes(Stream output, long count)
    {
        var zeroes = new byte[Math.Min(count, 1 << 16)];
        var written = 0L;
        while (written < count)
        {
            var n = (int)Math.Min(zeroes.Length, count - written);
            output.Write(zeroes, 0, n);
            written += n;
        }
    }

    /// <summary>
    /// 从 packed inode（fragment 特性）读取片段数据：packed_nid 指向特殊 packed inode，
    /// 其数据按普通文件映射（flat 主体 + 可能的 FLAT_INLINE 尾部内联），fragmentoff 为片段
    /// 在其中的逻辑字节偏移。严格对照内核 <c>fs/erofs/data.c</c> 对 packed inode 的处理：
    /// packed inode 是一个普通 flat/inline 文件，fragment 只是其数据区间。
    /// <para>Reads fragment data from the packed inode (fragments feature): s_packed_nid points to
    /// the special packed inode whose data is mapped like an ordinary file (flat body plus an
    /// optional FLAT_INLINE tail), and fragmentoff is the logical byte offset of the fragment
    /// within it. Mirrors the kernel's <c>fs/erofs/data.c</c> handling of the packed inode: it is
    /// an ordinary flat/inline file and a fragment is just one of its data ranges.</para>
    /// </summary>
    private void WritePackedData(ErofsInode fragmentInode, uint fragmentOff, Stream output)
    {
        var packedNid = _superblock.PackedNid;
        if (packedNid == 0)
        {
            throw new InvalidDataException("Fragment inode without a packed inode (s_packed_nid == 0).");
        }

        _packedInode ??= ReadInode(packedNid);
        var packed = _packedInode;
        if (!packed.IsRegularFile && !packed.IsDirectory)
        {
            throw new InvalidDataException($"Packed inode nid {packedNid} is not a regular file.");
        }

        var length = (long)fragmentInode.Size;
        if (length == 0)
        {
            return;
        }

        // packed inode 数据区映射：flat 布局（含 FLAT_INLINE）下，前 floor(size/blk)*blk 字节
        // 连续存放于 i_u 起始块，余数尾部内联在 inode + xattr 之后；纯 inline（i_u = 空洞
        // 哨兵）时全部数据都在 inode 之后。fragment 区间可能完全落在 flat 段、完全落在
        // inline 段，或跨两者，需分段复制。
        // The packed inode data mapping: under the flat layouts (incl. FLAT_INLINE) the first
        // floor(size/blk)*blk bytes live contiguously from the i_u block and the remainder is
        // inlined after the inode + xattrs; under pure inline (i_u = hole sentinel) all data sits
        // after the inode. A fragment range may fall entirely in the flat section, entirely in the
        // inline section, or straddle both, so it is copied in segments.
        var packedSize = (long)packed.Size;
        if (fragmentOff >= packedSize || length > packedSize - fragmentOff)
        {
            throw new InvalidDataException(
                $"Packed fragment range [{fragmentOff}..{fragmentOff + length}) exceeds the packed inode size {packedSize}.");
        }

        var inlineAll = packed.DataLayout == ErofsInode.DataLayoutInline &&
                        packed.RawBlockAddress == uint.MaxValue; // i_u = EROFS_NULL_ADDR
        if (packed.DataLayout == ErofsInode.DataLayoutInline && !inlineAll)
        {
            // FLAT_INLINE（tail-packing）：主体 flat 块 + 内联尾。
            // FLAT_INLINE (tail-packing): flat body plus an inlined tail.
            var flatLength = packedSize / _blockSize * _blockSize;
            WritePackedRange(fragmentOff, length, flatLength, packed, packedNid, inlineStartIsFlat: false, output);
            return;
        }

        if (packed.DataLayout == ErofsInode.DataLayoutInline)
        {
            // 纯 inline：全部数据在 inode 之后。Pure inline: everything after the inode.
            var start = _superblock.GetInodeOffset(packedNid) + packed.InlineDataOffset + fragmentOff;
            CheckRange(start, length, "Packed fragment data");
            CopyFromSource(output, start, length);
            return;
        }

        if (packed.DataLayout != ErofsInode.DataLayoutFlat)
        {
            throw new InvalidDataException($"Unsupported packed inode layout {packed.DataLayout} for nid {packedNid}.");
        }

        // 纯 flat：全部数据连续存放。Pure flat: all data is contiguous.
        WritePackedRange(fragmentOff, length, packedSize, packed, packedNid, inlineStartIsFlat: true, output);
    }

    /// <summary>
    /// 按 flat/inline 分段映射 packed inode 的 [<paramref name="offset"/>,
    /// <paramref name="offset"/> + <paramref name="length"/>) 区间并复制到输出。
    /// <para>Maps and copies the range [<paramref name="offset"/>,
    /// <paramref name="offset"/> + <paramref name="length"/>) of the packed inode across its flat
    /// and inline sections.</para>
    /// </summary>
    /// <param name="inlineStartIsFlat">纯 flat（无内联尾）时为 true，inline 起点即 flat 终点。
    /// True for pure-flat (no inlined tail): the inline start equals the flat end.</param>
    private void WritePackedRange(
        long offset,
        long length,
        long flatLength,
        ErofsInode packed,
        ulong packedNid,
        bool inlineStartIsFlat,
        Stream output)
    {
        var remaining = length;
        var pos = offset;
        if (pos < flatLength)
        {
            var flatBytes = Math.Min(remaining, flatLength - pos);
            var flatStart = checked(packed.RawBlockAddress * _blockSize + pos);
            CheckRange(flatStart, flatBytes, "Packed fragment data");
            CopyFromSource(output, flatStart, flatBytes);
            remaining -= flatBytes;
            pos += flatBytes;
        }

        if (remaining <= 0)
        {
            return;
        }

        // 内联段（FLAT_INLINE 尾或纯 inline 整体）。Inline section (FLAT_INLINE tail or the whole
        // pure-inline file). The inline section starts at InlineDataOffset; for FLAT_INLINE only the
        // data beyond flatLength is stored inline, so the in-file offset must be rebased.
        var inlineOffset = packed.InlineDataOffset;
        var inlineStart = _superblock.GetInodeOffset(packedNid) + inlineOffset;
        if (!inlineStartIsFlat)
        {
            // FLAT_INLINE：inline 起点对应文件偏移 flatLength。FLAT_INLINE: the inline section
            // corresponds to file offset flatLength.
            if (pos < flatLength)
            {
                throw new InvalidDataException("Packed fragment straddles the flat/inline boundary unexpectedly.");
            }

            inlineStart += pos - flatLength;
        }
        else
        {
            // 纯 flat 且调用方已保证无越界（flatLength == packed size）。Pure flat with the caller
            // having verified bounds (flatLength == packed size); defensive check only.
            if (pos >= flatLength)
            {
                throw new InvalidDataException("Packed fragment extends beyond a pure-flat packed inode.");
            }
        }

        CheckRange(inlineStart, remaining, "Packed fragment data");
        CopyFromSource(output, inlineStart, remaining);
    }

    /// <summary>
    /// 加载并解码单个逻辑簇条目（紧凑 4B/2B 位级索引或完整 8B 索引），严格对照内核
    /// <c>z_erofs_load_compact_lcluster</c> / <c>z_erofs_load_full_lcluster</c>。
    /// <para>Loads and decodes a single logical-cluster entry (compact bit-level 4B/2B indexes or full
    /// 8-byte indexes), strictly following the kernel <c>z_erofs_load_compact_lcluster</c> /
    /// <c>z_erofs_load_full_lcluster</c>.</para>
    /// </summary>
    private LclusterEntry LoadLcluster(
        ulong nid,
        ErofsInode inode,
        long headerOffset,
        ushort advise,
        int lclusterBits,
        int totalIdx,
        long lcn,
        out long ebase,
        out int compacted4bInitial,
        out int compacted2b)
    {
        // ebase：紧凑索引表起始（map header 之后）。ebase: start of the compact index table.
        ebase = headerOffset + ZErofsMapHeader.Size;
        if (inode.DataLayout == ErofsInode.DataLayoutCompactIndexes)
        {
            compacted4bInitial = (int)(((32 - ebase % 32) / 4) & 7);
            compacted2b = 0;
            if ((advise & ZErofsMapHeader.AdviseCompacted2B) != 0 && compacted4bInitial < totalIdx)
            {
                compacted2b = (totalIdx - compacted4bInitial) / 16 * 16;
            }

            return LoadCompactLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, compacted4bInitial, compacted2b, lcn);
        }

        compacted4bInitial = 0;
        compacted2b = 0;
        return LoadFullLcluster(inode, headerOffset, lclusterBits, lcn);
    }

    /// <summary>
    /// 解码紧凑索引中的单个逻辑簇条目（位级），移植自内核
    /// <c>z_erofs_load_compact_lcluster</c> + <c>decode_compactedbits</c>，含 big-pcluster 回看分支。
    /// <para>Decodes a single logical-cluster entry from the compact index (bit-level), ported from the
    /// kernel <c>z_erofs_load_compact_lcluster</c> + <c>decode_compactedbits</c>, including the
    /// big-pcluster lookback branch.</para>
    /// </summary>
    private LclusterEntry LoadCompactLcluster(
        ulong nid,
        ErofsInode inode,
        long headerOffset,
        ushort advise,
        int lclusterBits,
        int totalIdx,
        int compacted4bInitial,
        int compacted2b,
        long lcn)
    {
        var bigPcluster = (advise & (ZErofsMapHeader.AdviseBigPcluster1 | ZErofsMapHeader.AdviseBigPcluster2)) != 0;
        var amortizedShift = 2; // compact_4b：每簇 4 字节
        var pos = headerOffset + ZErofsMapHeader.Size;
        var l = lcn;
        if (l >= compacted4bInitial)
        {
            pos += compacted4bInitial * 4;
            l -= compacted4bInitial;
            if (l < compacted2b)
            {
                amortizedShift = 1; // compact_2b：每簇 2 字节
            }
            else
            {
                pos += compacted2b * 2;
                l -= compacted2b;
            }
        }

        pos += l * (1 << amortizedShift);

        int vcnt;
        if ((1 << amortizedShift) == 4 && lclusterBits <= 14)
        {
            vcnt = 2;
        }
        else if ((1 << amortizedShift) == 2 && lclusterBits <= 12)
        {
            vcnt = 16;
        }
        else
        {
            throw new InvalidDataException(
                $"Unsupported compact index layout (shift {amortizedShift}, lcluster bits {lclusterBits}).");
        }

        var packSize = vcnt << amortizedShift;
        var bytesInPack = (int)(pos & (packSize - 1));
        var i = bytesInPack >> amortizedShift;
        var packStartRel = (int)(pos - (headerOffset + ZErofsMapHeader.Size) - bytesInPack);
        if (packStartRel < 0)
        {
            throw new InvalidDataException("Compact index pack exceeds the image bounds.");
        }

        var ebase = headerOffset + ZErofsMapHeader.Size;
        var indexLength = checked((long)totalIdx * 4 + 64);
        if (indexLength > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Compact index table size {indexLength} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        var index = ReadBytes(ebase, checked((int)Math.Min(indexLength, _imageSize - ebase)), "compact index table");
        if (packStartRel + packSize > index.Length)
        {
            throw new InvalidDataException("Compact index pack exceeds the image bounds.");
        }

        var lobits = Math.Max(lclusterBits, 12); // ilog2(Z_EROFS_LI_D0_CBLKCNT) + 1 = 11 + 1
        var encodebits = ((packSize - 4) * 8) / vcnt;
        var (lo, type) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * i);
        if (type == LclusterTypeNonHead)
        {
            // NONHEAD：clusterofs = 1<<lclusterbits 哨兵；delta[0] 指向 HEAD 的距离。
            // NONHEAD: clusterofs = 1<<lclusterbits sentinel; delta[0] = distance to the HEAD.
            var clusterOfs = 1 << lclusterBits;
            if ((lo & (1u << 11)) != 0)
            {
                // D0_CBLKCNT 标志（big pcluster 首个 NONHEAD 存储压缩块数）。
                // D0_CBLKCNT flag: the first NONHEAD of a big pcluster stores the compressed block count.
                return new LclusterEntry(type, clusterOfs, delta0: 1, delta1: 0, pblk: 0, compressedBlocks: (int)(lo & ~(1u << 11)));
            }

            if (i + 1 == vcnt)
            {
                // pack 内最后一个条目存的是 delta[1]；delta[0] 由前一条目间接推出。
                // The last entry of the pack stores delta[1] instead; derive delta[0] from the previous one.
                var (prevLo, prevType) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * (i - 1));
                if (prevType != LclusterTypeNonHead)
                {
                    prevLo = 0;
                }
                else if ((prevLo & (1u << 11)) != 0)
                {
                    prevLo = 1;
                }

                return new LclusterEntry(type, clusterOfs, delta0: prevLo + 1, delta1: lo, pblk: 0, compressedBlocks: 0);
            }

            return new LclusterEntry(type, clusterOfs, delta0: lo, delta1: 0, pblk: 0, compressedBlocks: 0);
        }

        // HEAD / PLAIN：clusterofs = lo；pblk = pack 尾部 blkaddr + 本 pack 内回看累计的物理块数。
        // HEAD / PLAIN: clusterofs = lo; pblk = trailing blkaddr of the pack + lookback block count.
        var clusterOfsH = (int)lo;
        long nblk;
        var j = i;
        if (!bigPcluster)
        {
            nblk = 1;
            while (j > 0)
            {
                j--;
                var (prevLo2, prevType2) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * j);
                if (prevType2 == LclusterTypeNonHead)
                {
                    j -= (int)prevLo2;
                }

                if (j >= 0)
                {
                    nblk++;
                }
            }
        }
        else
        {
            // big-pcluster 回看：NONHEAD 带 CBLKCNT 时直接累加压缩块数，否则按 delta 回跳。
            // Big-pcluster lookback: NONHEAD with CBLKCNT accumulates compressed blocks; otherwise
            // jump back by delta.
            nblk = 0;
            while (j > 0)
            {
                j--;
                var (prevLo3, prevType3) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * j);
                if (prevType3 == LclusterTypeNonHead)
                {
                    if ((prevLo3 & (1u << 11)) != 0)
                    {
                        j--;
                        nblk += prevLo3 & ~(1u << 11);
                        continue;
                    }

                    // big-pcluster 不允许普通 d0 == 1；按 d0 - 2 回跳。
                    // Big pclusters must not have plain d0 == 1; jump back by d0 - 2.
                    if (prevLo3 <= 1)
                    {
                        throw new InvalidDataException($"Corrupt big-pcluster delta for nid {nid}.");
                    }

                    j -= (int)prevLo3 - 2;
                    continue;
                }

                nblk++;
            }
        }

        var packBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(packStartRel + packSize - 4, 4));
        var pblk = packBlkaddr + nblk;
        return new LclusterEntry(type, clusterOfsH, delta0: 0, delta1: 0, pblk: pblk, compressedBlocks: 0);
    }

    /// <summary>
    /// 解码完整（8 字节）压缩索引条目，移植自内核 <c>z_erofs_load_full_lcluster</c>。
    /// 完整索引起始于 <c>Z_EROFS_FULL_INDEX_START = Z_EROFS_MAP_HEADER_END + 8</c>。
    /// <para>Decodes a full (8-byte) compressed index entry, ported from the kernel
    /// <c>z_erofs_load_full_lcluster</c>. The full index starts at
    /// <c>Z_EROFS_FULL_INDEX_START = Z_EROFS_MAP_HEADER_END + 8</c>.</para>
    /// </summary>
    private LclusterEntry LoadFullLcluster(ErofsInode inode, long headerOffset, int lclusterBits, long lcn)
    {
        var indexOffset = headerOffset + ZErofsMapHeader.Size + FullIndexStartPad + lcn * ZErofsLclusterIndex.Size;
        if (indexOffset < 0 || indexOffset > _imageSize - ZErofsLclusterIndex.Size)
        {
            throw new InvalidDataException($"Full index entry {lcn} exceeds the image bounds.");
        }

        var entry = ZErofsLclusterIndex.FromBytes(ReadBytes(indexOffset, ZErofsLclusterIndex.Size, "full index entry"));
        if (entry.IsNonHead)
        {
            // D0_CBLKCNT 判定针对 delta[0] 值（内核 z_erofs_load_full_lcluster）。
            // The D0_CBLKCNT check applies to the delta[0] value (kernel z_erofs_load_full_lcluster).
            if ((entry.Delta0 & ZErofsLclusterIndex.LiD0CblkCnt) != 0)
            {
                return new LclusterEntry(LclusterTypeNonHead, 1 << lclusterBits, delta0: 1, delta1: 0, pblk: 0, compressedBlocks: entry.Delta0 & ~ZErofsLclusterIndex.LiD0CblkCnt);
            }

            return new LclusterEntry(LclusterTypeNonHead, 1 << lclusterBits, delta0: entry.Delta0, delta1: entry.Delta1, pblk: 0, compressedBlocks: 0);
        }

        var type = entry.IsHead2 ? LclusterTypeHead2 : (entry.IsPlain ? LclusterTypePlain : LclusterTypeHead1);
        if (entry.IsHole)
        {
            return new LclusterEntry(type, entry.ClusterOfs, delta0: 0, delta1: 0, pblk: -1, compressedBlocks: 0);
        }

        return new LclusterEntry(type, entry.ClusterOfs, delta0: 0, delta1: 0, pblk: entry.Blkaddr, compressedBlocks: 0);
    }

    /// <summary>
    /// 位级解码紧凑索引条目：<c>v = le32(in + pos/8) &gt;&gt; (pos &amp; 7)</c>；
    /// 低 <paramref name="lobits"/> 位为 lo（clusterofs / delta），随后 2 位为簇类型。
    /// <para>Bit-level compact index decode: <c>v = le32(in + pos/8) &gt;&gt; (pos &amp; 7)</c>;
    /// the low <paramref name="lobits"/> bits are lo (clusterofs / delta) and the following 2 bits
    /// are the cluster type.</para>
    /// </summary>
    private static (uint Lo, byte Type) DecodeCompactedBits(byte[] index, int packStartRel, int lobits, int bitPos)
    {
        var byteOffset = packStartRel + (bitPos >> 3);
        if (byteOffset + 4 > index.Length)
        {
            throw new InvalidDataException("Compact index bits exceed the image bounds.");
        }

        var v = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(byteOffset, 4)) >> (bitPos & 7);
        var lo = v & ((1u << lobits) - 1);
        var type = (byte)((v >> lobits) & 3);
        return (lo, type);
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// 解码后的逻辑簇条目。Decoded logical-cluster entry.
    /// </summary>
    private readonly struct LclusterEntry
    {
        public byte Type { get; }
        public int ClusterOfs { get; }
        public uint Delta0 { get; }
        public uint Delta1 { get; }
        public long Pblk { get; }
        public int CompressedBlocks { get; }

        public LclusterEntry(byte type, int clusterOfs, uint delta0, uint delta1, long pblk, int compressedBlocks)
        {
            Type = type;
            ClusterOfs = clusterOfs;
            Delta0 = delta0;
            Delta1 = delta1;
            Pblk = pblk;
            CompressedBlocks = compressedBlocks;
        }
    }
}