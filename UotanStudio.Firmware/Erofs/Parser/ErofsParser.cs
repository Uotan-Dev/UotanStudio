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
public sealed partial class ErofsParser : IDisposable
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

}
