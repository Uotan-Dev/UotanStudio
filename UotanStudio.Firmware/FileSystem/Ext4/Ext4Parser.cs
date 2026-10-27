using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Ext4.Models;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Ext4;

/// <summary>
/// Read-only parser for raw ext4 images. The image is treated as untrusted: superblock geometry, block size, feature flags, backup-group identity, group descriptor table size, inode numbers, directory depth, cycles and xattr values are bounds-checked before content is produced, and an optional JBD2 journal replay is folded in.
/// <para>原始 ext4 镜像的只读解析器。镜像视为不可信输入：超级块几何、块大小、特性标志、备份组标识、组描述符表大小、inode 号、目录深度、环路与 xattr 值在产出内容前均做上限检查，并可选折叠 JBD2 日志重放。</para>
/// </summary>
public sealed class Ext4Parser : IDisposable
{
    /// <summary>Classifies an entry found while walking the ext4 directory tree.
    /// <para>对 ext4 目录树遍历过程中发现的条目进行分类。</para></summary>
    public enum Ext4EntryKind
    {
        /// <summary>Regular file, or a special node reported as a file. <para>常规文件，或以文件形式上报的特殊节点。</para></summary>
        File,

        /// <summary>Directory node. <para>目录节点。</para></summary>
        Directory,

        /// <summary>Symbolic link whose decoded target sits in <c>SymlinkTarget</c>. <para>符号链接，其解码后的目标位于 <c>SymlinkTarget</c>。</para></summary>
        Symlink,
    }

    /// <summary>
    /// One filesystem entry collected by the directory walk, identified by its relative path.
    /// <para>目录树遍历收集到的单个文件系统条目，以相对路径标识。</para>
    /// </summary>
    /// <param name="Path">Slash-separated path relative to the filesystem root, without a leading "./". <para>相对文件系统根目录、以 <c>/</c> 分隔的路径，不含开头的 "./"。</para></param>
    /// <param name="Size">Logical inode size in bytes as stored in the image. <para>镜像中记录的 inode 逻辑字节大小。</para></param>
    /// <param name="Inode">Inode number within the image. <para>镜像内的 inode 编号。</para></param>
    /// <param name="Kind">Entry type classification. <para>条目类型分类。</para></param>
    /// <param name="SymlinkTarget">Decoded UTF-8 link target, or <see langword="null"/> for non-symlink entries. <para>解码后的 UTF-8 链接目标；非符号链接条目为 <see langword="null"/>。</para></param>
    /// <param name="Xattrs">Inline and external extended attributes merged under full names, or <see langword="null"/> when the inode has none; values are capped at 64 KiB. <para>已按完整名称合并的内联与外部扩展属性；inode 无属性时为 <see langword="null"/>；值上限为 64 KiB。</para></param>
    /// <param name="Uid">Owner user identifier from the inode. <para>来自 inode 的所有者用户 ID。</para></param>
    /// <param name="Gid">Owner group identifier from the inode. <para>来自 inode 的所有者组 ID。</para></param>
    /// <param name="Mode">Raw inode mode including the file-type bits. <para>含文件类型位的原始 inode 模式。</para></param>
    /// <param name="DeviceId">Device number read from the inode's first non-zero direct block pointer slot; defined for device nodes only. <para>取自 inode 首个非零直接块指针槽的设备及；仅对设备节点有定义。</para></param>
    /// <param name="Flags">Raw inode flags as stored in the image. <para>镜像中记录的原始 inode 标志。</para></param>
    public sealed record Ext4FileEntry(string Path, long Size, uint Inode, Ext4EntryKind Kind, string? SymlinkTarget = null, IReadOnlyDictionary<string, byte[]>? Xattrs = null, uint Uid = 0, uint Gid = 0, ushort Mode = 0, uint DeviceId = 0, uint Flags = 0)
    {
        /// <summary>Gets the source timestamps and inode identity attributes.
        /// <para>获取源时间戳与 inode 身份属性。</para></summary>
        /// <value>The metadata preserved in the inode's extra space, or <see langword="null"/> when the inode carries none.
        /// <para>保留在 inode 额外空间中的元数据；inode 未携带时为 <see langword="null"/>。</para></value>
        public FileSystemInodeMetadata? Metadata { get; init; }
    }

    private const int SuperblockReadLength = 1024 + 1024;
    private const uint RootInodeNumber = 2;
    private const int MaxExtentLeafPointers = 12;
    private const long MaximumGroupDescriptorTableSize = 512L * 1024 * 1024;
    private const int MaxDirectoryDepth = 256;

    private const uint FeatureIncompatEaInode = 0x0400;

    private const uint FeatureRoCompatMetadataCsum = 0x0400;

    private const int MaxXattrValueSize = 64 * 1024;

    private static readonly uint[] SparseSuperGroupBases = [3, 5, 7];

    private static readonly string[] XattrNameIndexPrefixes =
    [
        "",
        "user.",
        "system.posix_acl_access",
        "system.posix_acl_default",
        "trusted.",
        "lustre.",
        "security.",
        "system.",
        "system.richacl",
        "encryption.",
    ];

    private readonly Stream _stream;
    private Ext4Superblock _superblock = null!;
    private Ext4BlockGroup[] _groups = [];
    private int _blockSize;
    private int _inodeSize;
    private long _inodeTableOffset;
    private byte[] _inodeBuffer = [];
    private readonly Dictionary<string, Ext4FileEntry> _entries = new(StringComparer.Ordinal);
    private bool _walked;
    private readonly int _maximumEntries = int.MaxValue;
    private readonly long _maximumPathCharacters = long.MaxValue;
    private readonly CancellationToken _readCancellation;
    private int _visitedEntries;
    private long _pathCharacters;
    private Journal.JournalOverlay _journalOverlay = Journal.JournalOverlay.Empty;

    private long _cacheTick;

    /// <summary>
    /// Bounded cache of metadata blocks (indirect pointer blocks and extent index/leaf blocks)
    /// that are re-read once per logical block during sequential file reads. When over capacity a
    /// single least-recently-used block is evicted; parser instances are single-threaded by usage model.
    /// <para>元数据块（间接指针块与 extent 索引/叶子块）的有界缓存——顺序读取大文件时这些块
    /// 会被逐逻辑块重复读取；超容量时按最近最少使用逐出一个块。解析器实例按现有使用模型为单线程。</para>
    /// </summary>
    private readonly Dictionary<ulong, (byte[] Data, long Stamp)> _metadataBlockCache = new();

    /// <summary>
    /// Maximum number of metadata blocks retained in <see cref="_metadataBlockCache"/>.
    /// <para><see cref="_metadataBlockCache"/> 保留的元数据块数量上限。</para>
    /// </summary>
    private const int MaxCachedMetadataBlocks = 64;

    /// <summary>
    /// Opens an ext4 image file for parsing.
    /// <para>打开 ext4 镜像文件进行解析。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Path to a readable raw ext4 image; the file is opened for shared reading and stays open until <see cref="Dispose"/>.
    /// <para>可读的 ext4 原始镜像路径；文件以共享读方式打开，直到 <see cref="Dispose"/> 才关闭。</para>
    /// </param>
    public Ext4Parser(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        _stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        Initialize();
    }

    /// <summary>
    /// Parses an ext4 image from an existing stream (ownership is transferred).
    /// <para>从既有流解析 ext4 镜像（所有权转移）。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable image stream with unbounded entry and path budgets; it is closed by <see cref="Dispose"/>.
    /// <para>条目与路径预算不受限制的可读、可寻址镜像流；由 <see cref="Dispose"/> 负责关闭。</para>
    /// </param>
    public Ext4Parser(Stream stream) : this(stream, int.MaxValue, long.MaxValue, CancellationToken.None)
    {
    }

    /// <summary>Parses a stream with explicit directory-entry/path budgets and cancellable metadata/content reads.
    /// <para>按显式的目录条目/路径预算解析流，元数据与内容读取可取消。</para></summary>
    /// <param name="stream">Readable and seekable stream; ownership transfers and it is closed by <see cref="Dispose"/>. A non-readable or non-seekable stream throws <see cref="ArgumentException"/>.
    /// <para>可读且可寻址的流；所有权转移并由 <see cref="Dispose"/> 关闭。流不可读或不可寻址时抛出 <see cref="ArgumentException"/>。</para></param>
    /// <param name="maximumEntries">Positive upper bound on visited directory entries; exceeding it during a walk throws <see cref="FileSystemCorruptedException"/>.
    /// <para>已访问目录条目数的正整数上限；遍历中超出时抛出 <see cref="FileSystemCorruptedException"/>。</para></param>
    /// <param name="maximumPathCharacters">Positive upper bound on the accumulated total of all entry path lengths.
    /// <para>所有条目路径长度累计总和的正整数上限。</para></param>
    /// <param name="cancellationToken">Cancels initialization and every subsequent metadata or content read; an already-cancelled token throws immediately.
    /// <para>取消初始化及其后所有元数据/内容读取；传入已取消的令牌会立即抛出。</para></param>
    public Ext4Parser(Stream stream, int maximumEntries, long maximumPathCharacters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPathCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        _maximumEntries = maximumEntries;
        _maximumPathCharacters = maximumPathCharacters;
        _readCancellation = cancellationToken;
        Initialize();
    }

    /// <summary>Total filesystem size derived from the superblock.
    /// <para>由超级块推导的文件系统总字节大小。</para></summary>
    /// <value>Block count multiplied by block size, with overflow checking.
    /// <para>总块数乘以块大小，带溢出检查。</para></value>
    public long PartitionSize => checked((long)_superblock.BlocksCount * _blockSize);

    internal byte[] SourceUuid => _superblock.Uuid;
    internal string SourceVolumeName => _superblock.VolumeName;

    /// <summary>The parsed superblock used for metadata inspection. <para>用于元数据检视的已解析超级块。</para></summary>
    internal Ext4Superblock Superblock => _superblock;

    /// <summary>Filesystem block size in bytes.
    /// <para>文件系统块大小（字节）。</para></summary>
    /// <value>A power-of-two size between 1024 and 65536; images reporting any other value are rejected at open time.
    /// <para>1024 到 65536 之间的 2 的幂；报告其他取值的镜像在打开时即被拒绝。</para></value>
    public long BlockSize => _blockSize;

    /// <summary>Total block count reported by the superblock.
    /// <para>超级块报告的总块数。</para></summary>
    /// <value>The filesystem's logical block capacity, independent of the underlying stream length.
    /// <para>文件系统的逻辑块容量，与底层流长度无关。</para></value>
    public ulong BlockCount => _superblock.BlocksCount;

    /// <summary>Total inode count reported by the superblock.
    /// <para>超级块报告的 inode 总数。</para></summary>
    /// <value>The addressable inode upper bound; inode numbers run from 1 to this value.
    /// <para>可寻址的 inode 上界；inode 编号范围为 1 到该值。</para></value>
    public uint InodeCount => _superblock.InodesCount;

    /// <summary>
    /// Cached counts computed once after the directory walk, replacing the per-call LINQ
    /// full-table scan.
    /// <para>目录遍历后一次性缓存的计数，替代每次调用的 LINQ 全表遍历。</para>
    /// </summary>
    private int? _fileCountCache;
    private int? _directoryCountCache;

    /// <summary>
    /// Ensures the directory tree has been walked once before reading cached counters.
    /// <para>读取缓存计数前确保目录树已遍历一次。</para>
    /// </summary>
    private void EnsureWalked()
    {
        if (!_walked)
        {
            WalkDirectory(RootInodeNumber, string.Empty, new HashSet<uint>(), 0);
            _walked = true;
        }
    }

    /// <summary>Number of non-directory entries in the image.
    /// <para>镜像中非目录条目的数量。</para></summary>
    /// <value>A cached count; reading it first walks the whole directory tree if that has not happened yet.
    /// <para>缓存计数；若尚未遍历，首次读取会先完整遍历目录树。</para></value>
    public int FileCount
    {
        get
        {
            EnsureWalked();
            _fileCountCache ??= _entries.Values.Count(e => e.Kind != Ext4EntryKind.Directory);
            return _fileCountCache.Value;
        }
    }

    /// <summary>Number of directory entries in the image, excluding the root.
    /// <para>镜像中目录条目的数量，不含根目录。</para></summary>
    /// <value>A cached count; reading it first walks the whole directory tree if that has not happened yet.
    /// <para>缓存计数；若尚未遍历，首次读取会先完整遍历目录树。</para></value>
    public int DirectoryCount
    {
        get
        {
            EnsureWalked();
            _directoryCountCache ??= _entries.Values.Count(e => e.Kind == Ext4EntryKind.Directory);
            return _directoryCountCache.Value;
        }
    }

    private void Initialize()
    {
        _stream.Position = 0;
        var prefix = new byte[SuperblockReadLength];
        _stream.ReadExactly(prefix);
        _superblock = Ext4Superblock.FromBytes(prefix);
        _blockSize = checked((int)_superblock.BlockSize);
        if (_blockSize is not (1024 or 2048 or 4096 or 8192 or 16384 or 32768 or 65536))
        {
            throw new FileSystemUnsupportedFeatureException($"Unsupported ext4 block size {_blockSize}.");
        }

        const uint supportedIncompat = 0x0001 | 0x0002 | 0x0004
            | 0x0040 | 0x0080 | 0x0200 | 0x0400
            | 0x2000 | 0x4000 | 0x8000 | 0x10000 | 0x20000;

        var unsupportedIncompat = _superblock.FeatureIncompat & ~supportedIncompat;
        if (unsupportedIncompat != 0)
        {
            throw new FileSystemUnsupportedFeatureException($"ext4 image has unsupported incompatible features: 0x{unsupportedIncompat:X8}.");
        }
        if ((_superblock.FeatureIncompat & 0x20000) != 0 && _superblock.Encoding != 1)
        {
            throw new NotSupportedException($"Unsupported EXT4 casefold encoding id {_superblock.Encoding}.");
        }

        _inodeSize = _superblock.InodeSize == 0 ? 256 : _superblock.InodeSize;
        _inodeBuffer = new byte[_inodeSize];
        var descSize = _superblock.EffectiveDescSize;
        var groupCount = checked((int)((_superblock.BlocksCount + _superblock.BlocksPerGroup - 1) / _superblock.BlocksPerGroup));
        if (groupCount <= 0)
        {
            throw new FileSystemCorruptedException("ext4 image has no block groups.");
        }

        var gdtLength = checked((long)groupCount * descSize);

        if (_superblock.HasSparseSuper2)
        {
            if (_superblock.FirstDataBlock != 0)
            {
                throw new FileSystemCorruptedException(
                    $"ext4 sparse_super2 requires s_first_data_block to be 0, found {_superblock.FirstDataBlock}.");
            }

            if (_superblock.BackupGroup1 >= groupCount || _superblock.BackupGroup2 >= groupCount)
            {
                throw new FileSystemCorruptedException(
                    $"ext4 sparse_super2 backup group ids {_superblock.BackupGroup1} and {_superblock.BackupGroup2} exceed the {groupCount} block groups of the image.");
            }
        }

        var gdtOffset = checked((_superblock.FirstDataBlock + 1) * _blockSize);
        if (gdtLength > MaximumGroupDescriptorTableSize || gdtLength > _stream.Length - gdtOffset)
        {
            throw new FileSystemCorruptedException("ext4 group descriptor table exceeds the image bounds or supported metadata limit.");
        }
        _groups = new Ext4BlockGroup[groupCount];
        var descriptorsPerRead = Math.Min(groupCount, Math.Max(1, 65536 / descSize));
        var gdtBytes = new byte[checked(descriptorsPerRead * descSize)];
        _stream.Position = gdtOffset;
        for (var first = 0; first < groupCount; first += descriptorsPerRead)
        {
            var count = Math.Min(descriptorsPerRead, groupCount - first);
            _stream.ReadExactly(gdtBytes.AsSpan(0, count * descSize));
            for (var i = 0; i < count; i++)
                _groups[first + i] = Ext4BlockGroup.FromBytes(gdtBytes.AsSpan(i * descSize, descSize), descSize);
        }

        _inodeTableOffset = checked((long)_groups[0].InodeTable * _blockSize);
        if (_inodeTableOffset < 0 || _inodeTableOffset >= _stream.Length)
        {
            throw new FileSystemCorruptedException("ext4 inode table lies outside the image bounds.");
        }

        if ((_superblock.FeatureCompat & 0x4) != 0 && (_superblock.FeatureIncompat & 0x4) != 0)
        {
            try
            {
                _journalOverlay = ReplayJournal();
            }
            catch (Exception ex)
            {

                System.Diagnostics.Debug.WriteLine($"ext4 journal replay failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reports whether one block group stores a backup superblock and descriptor table copy.
    /// <para>判断某个块组是否保存超级块与组描述符表备份。</para>
    /// </summary>
    /// <param name="group">Zero-based block group index. <para>从零开始的块组编号。</para></param>
    /// <returns><see langword="true"/> when the group stores superblock copies; otherwise <see langword="false"/>.
    /// <para>该组保存超级块备份时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool GroupHasSuperblock(uint group)
    {
        if (group == 0)
        {
            return true;
        }
        if (_superblock.HasSparseSuper2)
        {
            return group == _superblock.BackupGroup1 || group == _superblock.BackupGroup2;
        }
        if (!_superblock.HasSparseSuper)
        {
            return false;
        }
        if (group is 1 or 3 or 5 or 7)
        {
            return true;
        }
        foreach (var @base in SparseSuperGroupBases)
        {
            for (var power = @base; power <= group && power <= uint.MaxValue / @base; power *= @base)
            {
                if (power == group)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Walks the directory tree from the root inode (2) and returns all entries (files, directories and symlinks) with their relative paths.
    /// <para>从根 inode（2）遍历目录树，返回所有条目（文件、目录与符号链接）及其相对路径。</para>
    /// </summary>
    /// <returns>A snapshot list of every non-root entry in walk order; the root itself is not included.
    /// <para>除根目录外全部条目的快照列表，按遍历顺序排列；不包含根本身。</para></returns>
    public IReadOnlyList<Ext4FileEntry> ListFiles()
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (!_walked)
        {
            WalkDirectory(RootInodeNumber, string.Empty, new HashSet<uint>(), 0);
            _walked = true;
        }

        return [.. _entries.Values];
    }

    /// <summary>Includes root inode metadata without changing the file-browser listing contract.
    /// <para>在保留文件浏览器列表现有契约的同时，附带根 inode 的元数据。</para>
    /// </summary>
    /// <returns>The root entry under path <c>"."</c> followed by the <see cref="ListFiles"/> snapshot.
    /// <para>以 <c>"."</c> 为路径的根条目，其后跟随 <see cref="ListFiles"/> 的快照。</para></returns>
    public IReadOnlyList<Ext4FileEntry> ListEntriesWithRoot()
    {
        var entries = ListFiles();
        var root = ReadInode(RootInodeNumber);
        var xattrs = MergeXattrs(ParseInlineXattrs(_inodeBuffer, _inodeSize), ReadExternalXattrBlock(root));
        return [new Ext4FileEntry(".", root.Size, RootInodeNumber, Ext4EntryKind.Directory, Xattrs: xattrs, Uid: root.Uid, Gid: root.Gid, Mode: root.Mode, Flags: root.Flags) { Metadata = root.Metadata }, .. entries];
    }

    /// <summary>
    /// Writes the full content of the file at path to output, zero-filling sparse holes.
    /// <para>将 path 处文件的完整内容写入 output， 稀疏洞补零；符号链接输出其目标字节。</para>
    /// </summary>
    /// <param name="path">Exact stored path as returned by <see cref="ListFiles"/>; a missing path throws <see cref="FileNotFoundException"/> and a directory is rejected with <see cref="InvalidOperationException"/>.
    /// <para>必须是 <see cref="ListFiles"/> 返回的精确存储路径；路径不存在时抛出 <see cref="FileNotFoundException"/>，目录会以 <see cref="InvalidOperationException"/> 拒绝。</para></param>
    /// <param name="output">Writable destination stream written at its current position; it is never flushed or closed here.
    /// <para>按当前位置写入的可写目标流；本方法不刷新也不关闭它。</para></param>
    public void ReadFile(string path, Stream output)
    {
        _readCancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(output);
        var entry = FindEntry(path);

        if (entry.Kind == Ext4EntryKind.Directory)
        {
            throw new InvalidOperationException($"Cannot read a directory as a file: '{path}'.");
        }

        if (entry.Kind == Ext4EntryKind.Symlink)
        {
            var symlinkInode = ReadInode(entry.Inode);
            var targetBytes = Encoding.UTF8.GetBytes(ReadSymlinkTarget(symlinkInode) ?? string.Empty);
            output.Write(targetBytes);
            return;
        }

        if (entry.Size == 0)
        {
            return;
        }

        var inode = ReadInode(entry.Inode);
        if (inode.HasInlineData)
        {
            var length = (int)Math.Min(entry.Size, inode.InlineData.Length);
            if (length > 0)
            {
                output.Write(inode.InlineData, 0, length);
            }

            return;
        }

        ReadInodeContent(inode, entry.Size, output);
    }

    /// <summary>
    /// Extracts the whole filesystem tree into outputDirectory.
    /// <para>将整个文件系统树提取到 outputDirectory。</para>
    /// </summary>
    /// <param name="outputDirectory">Host directory that receives the tree; created when absent. <para>接收目录树的主机目录；不存在时创建。</para></param>
    /// <param name="overwrite">When <c>false</c>, host files that already exist are skipped. <para>为 <c>false</c> 时跳过主机上已存在的文件。</para></param>
    /// <param name="progress">Reports processed/total entry counts; <see langword="null"/> disables reporting. <para>报告 已处理条目数/总条目数；<see langword="null"/> 表示不报告。</para></param>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
        => ExtractToDirectory(outputDirectory, null, CancellationToken.None, overwrite, progress);

    /// <summary>Extracts with measured output bytes and cancellation, including writes within a large file.
    /// <para>带字节计量与取消的提取，取消粒度可深入单个大文件的写入过程。</para>
    /// </summary>
    /// <param name="outputDirectory">Host directory that receives the tree; created when absent. <para>接收目录树的主机目录；不存在时创建。</para></param>
    /// <param name="byteProgress">Receives counted output bytes against the estimated total; symlink entries contribute their UTF-8 target length. <para>接收相对估算总量的已计输出字节；符号链接条目按其目标的 UTF-8 长度计入。</para></param>
    /// <param name="cancellationToken">Checked before each entry and inside content writes; extraction is not rolled back on cancellation. <para>在每个条目前及内容写入过程中检查；取消时不回滚已提取内容。</para></param>
    /// <param name="overwrite">When <c>false</c>, host files that already exist are skipped. <para>为 <c>false</c> 时跳过主机上已存在的文件。</para></param>
    /// <param name="progress">Reports processed/total entry counts; <see langword="null"/> disables reporting. <para>报告 已处理条目数/总条目数；<see langword="null"/> 表示不报告。</para></param>
    public void ExtractToDirectory(string outputDirectory, IProgress<(long Processed, long Total)>? byteProgress,
        CancellationToken cancellationToken, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var entries = ListFiles();
        var totalCount = entries.Count;
        var hostPaths = new Extraction.PortableExtractionPaths(outputDirectory, entries.Select(entry => entry.Path));
        var totalBytes = entries.Where(e => e.Kind != Ext4EntryKind.Directory
                && (e.Kind == Ext4EntryKind.Symlink || overwrite || !File.Exists(hostPaths.Resolve(e.Path))))
            .Sum(e => e.Kind == Ext4EntryKind.Symlink ? Encoding.UTF8.GetByteCount(e.SymlinkTarget ?? string.Empty)
                : (e.Mode & 0xF000) == 0x8000 ? e.Size : 0L);
        var bytes = new Core.ExtractionByteCounter(totalBytes, byteProgress, cancellationToken);
        var processedCount = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = hostPaths.Resolve(entry.Path);
            if (entry.Kind == Ext4EntryKind.Directory)
            {
                Directory.CreateDirectory(targetPath);
                processedCount++;
                progress?.Report((processedCount, totalCount));
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            if (entry.Kind == Ext4EntryKind.Symlink)
            {
                Core.FileExtraction.WriteAtomic(targetPath,
                    output => { using var counted = bytes.Wrap(output); counted.Write(Encoding.UTF8.GetBytes(entry.SymlinkTarget ?? string.Empty)); }, overwrite);

                processedCount++;
                progress?.Report((processedCount, totalCount));
                continue;
            }

            if (File.Exists(targetPath) && !overwrite)
            {
                processedCount++;
                progress?.Report((processedCount, totalCount));
                continue;
            }

            Core.FileExtraction.WriteAtomic(targetPath, output => { using var counted = bytes.Wrap(output); ReadFile(entry.Path, counted); }, overwrite);

            processedCount++;
            progress?.Report((processedCount, totalCount));
        }
        bytes.Complete();
    }

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    public void Dispose()
    {
        _stream.Dispose();
    }

    private Ext4FileEntry FindEntry(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ListFiles();
        if (!_entries.TryGetValue(path, out var entry))
        {
            throw new FileNotFoundException($"No such file in ext4 image: '{path}'.", path);
        }

        return entry;
    }

    private void WalkDirectory(uint dirInode, string prefix, HashSet<uint> visitedDirectories, int depth)
    {
        _readCancellation.ThrowIfCancellationRequested();

        if (depth > MaxDirectoryDepth)
        {
            throw new FileSystemCorruptedException($"ext4 directory tree exceeds the maximum depth of {MaxDirectoryDepth}.");
        }

        if (!visitedDirectories.Add(dirInode))
        {

            throw new FileSystemCorruptedException($"ext4 directory cycle detected at inode {dirInode}.");
        }

        var inode = ReadInode(dirInode);
        var blockCount = inode.HasInlineData ? 2u : (uint)((inode.Size + _blockSize - 1) / _blockSize);
        for (var logical = 0u; logical < blockCount; logical++)
        {
            byte[] block;
            if (inode.HasInlineData)
            {
                block = logical == 0 ? inode.InlineData.AsSpan(4, 56).ToArray() : inode.InlineData.AsSpan(60).ToArray();
            }
            else
            {
                var physical = MapBlock(inode, logical);
                if (physical is null) continue;
                block = ReadBlock((ulong)physical);
            }
            foreach (var entry in Ext4DirectoryEntry.Enumerate(block))
            {
                _readCancellation.ThrowIfCancellationRequested();
                if (entry.Inode == 0 || entry.Name.Length == 0)
                {
                    continue;
                }

                if (entry.Name is "." or "..")
                {
                    continue;
                }

                if (!IsSafeName(entry.Name))
                {
                    continue;
                }

                var childPath = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if (_visitedEntries >= _maximumEntries || childPath.Length > _maximumPathCharacters - _pathCharacters)
                {
                    throw new FileSystemCorruptedException("ext4 directory enumeration exceeds its entry or path budget.");
                }
                _visitedEntries++;
                _pathCharacters += childPath.Length;
                var child = ReadInode(entry.Inode);
                var childXattrs = MergeXattrs(
                    ParseInlineXattrs(_inodeBuffer, _inodeSize),
                    ReadExternalXattrBlock(child));
                var kind = ClassifyKind(entry.FileType, child);
                var symlinkTarget = kind == Ext4EntryKind.Symlink ? ReadSymlinkTarget(child) : null;

                _entries[childPath] = new Ext4FileEntry(childPath, child.Size, entry.Inode, kind, symlinkTarget, childXattrs, child.Uid, child.Gid, child.Mode, child.BlockPointers[0] != 0 ? child.BlockPointers[0] : child.BlockPointers[1], child.Flags) { Metadata = child.Metadata };
                if (kind == Ext4EntryKind.Directory)
                {
                    WalkDirectory(entry.Inode, childPath, visitedDirectories, depth + 1);
                }
            }
        }
    }

    private static Ext4EntryKind ClassifyKind(byte fileType, Ext4Inode inode)
        => fileType switch
        {
            Ext4DirectoryEntry.TypeRegular => Ext4EntryKind.File,
            Ext4DirectoryEntry.TypeDirectory => Ext4EntryKind.Directory,
            Ext4DirectoryEntry.TypeSymlink => Ext4EntryKind.Symlink,
            _ => inode.IsDirectory ? Ext4EntryKind.Directory : inode.IsSymlink ? Ext4EntryKind.Symlink : Ext4EntryKind.File,
        };

    private static bool IsSafeName(string name)
        => !name.Contains('/') && !name.Contains('\0') && name != "." && name != "..";

    /// <summary>
    /// Parses inline extended attributes (xattrs) from an inode's extra space.
    /// <para>从 inode 的额外空间解析内联扩展属性（xattr）。</para>
    /// </summary>
    private Dictionary<string, byte[]>? ParseInlineXattrs(byte[] inodeRaw, int inodeSize)
    {

        if (inodeSize <= 132)
        {
            return null;
        }

        var extraIsiz = BinaryPrimitives.ReadUInt16LittleEndian(inodeRaw.AsSpan(128, 2));
        if (extraIsiz < 4 || 128 + extraIsiz + 4 > inodeSize)
        {
            return null;
        }

        var xattrStart = 128 + extraIsiz;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(inodeRaw.AsSpan(xattrStart, 4));
        if (magic != Ext4Xattr.Magic)
        {
            return null;
        }

        return ReadXattrEntries(inodeRaw.AsSpan(0, inodeSize).ToArray(), xattrStart + 4, xattrStart + 4);
    }

    /// <summary>
    /// Merges inline and external xattr dictionaries into a single read-only result.
    /// <para>将内联与外部 xattr 字典合并为单一只读结果。</para>
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]>? MergeXattrs(
        Dictionary<string, byte[]>? inline,
        Dictionary<string, byte[]>? external)
    {
        if (inline is null && external is null)
        {
            return null;
        }

        var merged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (inline is not null)
        {
            foreach (var kv in inline)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        if (external is not null)
        {
            foreach (var kv in external)
            {
                if (!merged.TryAdd(kv.Key, kv.Value))
                {
                    throw new FileSystemCorruptedException($"Duplicate EXT4 xattr: {kv.Key}");
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Reads the external extended attribute block pointed to by the inode's Ext4Inode.FileAcl field.
    /// <para>读取由 inode 的 Ext4Inode.FileAcl 字段指向的外部扩展属性块。</para>
    /// </summary>
    private Dictionary<string, byte[]>? ReadExternalXattrBlock(Ext4Inode inode)
    {
        if (inode.FileAcl == 0)
        {
            return null;
        }

        var block = ReadBlock(inode.FileAcl);

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0, 4));
        if (magic != Ext4Xattr.Magic)
        {
            throw new FileSystemCorruptedException("Invalid EXT4 external xattr magic.");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(8)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4)) == 0)
        {
            throw new FileSystemCorruptedException("Invalid EXT4 external xattr block header.");
        }
        return ReadXattrEntries(block, 32, 0);
    }

    private Dictionary<string, byte[]>? ReadXattrEntries(byte[] bytes, int first, int valueBase)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var position = first;
        var firstValue = bytes.Length;
        while (position <= bytes.Length - 4)
        {
            _readCancellation.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)) == 0)
            {
                if (position + 4 > firstValue)
                {
                    throw new FileSystemCorruptedException("EXT4 xattr entries overlap their values.");
                }
                return result.Count == 0 ? null : result;
            }
            if (position > bytes.Length - 16)
            {
                throw new FileSystemCorruptedException("Truncated EXT4 xattr entry.");
            }
            var nameLength = bytes[position];
            var entryLength = (16 + nameLength + 3) & ~3;
            if (entryLength > bytes.Length - position)
            {
                throw new FileSystemCorruptedException("Truncated EXT4 xattr name.");
            }
            var name = ResolveXattrName(bytes[position + 1], Encoding.UTF8.GetString(bytes, position + 16, nameLength));
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 2));
            var inode = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8));
            if (size > MaxXattrValueSize)
            {
                throw new FileSystemCorruptedException("EXT4 xattr value exceeds the supported 64 KiB limit.");
            }
            byte[] value;
            if (inode != 0)
            {
                if ((_superblock.FeatureIncompat & FeatureIncompatEaInode) == 0 || offset != 0)
                {
                    throw new FileSystemCorruptedException("Invalid EXT4 EA inode reference.");
                }
                value = ReadEaInodeValue(inode, size);
            }
            else
            {
                var absolute = valueBase + offset;
                if (size != 0 && (absolute < first || absolute > bytes.Length || size > bytes.Length - absolute))
                {
                    throw new FileSystemCorruptedException("EXT4 xattr value exceeds its container.");
                }
                if (size != 0)
                {
                    firstValue = Math.Min(firstValue, absolute);
                }
                value = size == 0 ? [] : bytes.AsSpan(absolute, (int)size).ToArray();
            }
            if (!result.TryAdd(name, value))
            {
                throw new FileSystemCorruptedException($"Duplicate EXT4 xattr: {name}");
            }
            position += entryLength;
        }
        throw new FileSystemCorruptedException("Missing EXT4 xattr entry terminator.");
    }

    /// <summary>
    /// Builds the full xattr name from the name_index byte and the entry name suffix.
    /// <para>根据 name_index 字节与条目名称后缀构建完整 xattr 名称。</para>
    /// </summary>
    private static string ResolveXattrName(byte nameIndex, string suffix)
    {
        if (nameIndex < XattrNameIndexPrefixes.Length)
        {
            var prefix = XattrNameIndexPrefixes[nameIndex];

            if (nameIndex is 2 or 3)
            {
                return prefix;
            }

            return prefix + suffix;
        }

        throw new NotSupportedException($"Unknown EXT4 xattr namespace index {nameIndex}.");
    }

    /// <summary>
    /// Reads the value data from a dedicated ea_inode when a large xattr value is stored in its own inode.
    /// <para>当大型 xattr 值存于其自身 inode 时，从专用 ea_inode 读取值数据。</para>
    /// </summary>
    private byte[] ReadEaInodeValue(uint eaInodeNumber, uint expectedSize)
    {
        if (eaInodeNumber == 0)
        {
            throw new FileSystemCorruptedException("Invalid EXT4 EA inode number.");
        }

        var eaInode = ReadInode(eaInodeNumber);
        if (eaInode.Size != expectedSize || eaInode.Size > MaxXattrValueSize
            || (eaInode.Flags & 0x0020_0000) == 0)
        {
            throw new FileSystemCorruptedException("Invalid EXT4 EA inode size or flags.");
        }
        var size = checked((int)eaInode.Size);
        if (size <= 0)
        {
            return [];
        }

        using var ms = new MemoryStream(size);
        ReadInodeContent(eaInode, size, ms);
        return ms.GetBuffer().AsSpan(0, (int)ms.Length).ToArray();
    }

    private string? ReadSymlinkTarget(Ext4Inode inode)
    {
        if (inode.IsSymlink && !inode.HasExtents && inode.Size < 60)
        {

            return Encoding.UTF8.GetString(inode.BlockData, 0, (int)inode.Size);
        }

        using var ms = new MemoryStream();
        ReadInodeContent(inode, inode.Size, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void ReadInodeContent(Ext4Inode inode, long size, Stream output)
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (inode.HasInlineData)
        {
            var length = (int)Math.Min(size, inode.InlineData.Length);
            if (length > 0)
            {
                output.Write(inode.InlineData, 0, length);
            }

            return;
        }

        if (!_journalOverlay.HasOverlay)
        {
            Core.Streams.MappedBlockCopy.Copy(_stream, output, size, _blockSize, logical => MapBlock(inode, logical), _readCancellation);
            return;
        }

        var blockCount = (uint)((size + _blockSize - 1) / _blockSize);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            var remaining = size;
            for (var logical = 0u; logical < blockCount; logical++)
            {
                _readCancellation.ThrowIfCancellationRequested();
                var physical = MapBlock(inode, logical);
                buffer.AsSpan(0, _blockSize).Clear();
                if (physical is not null)
                {
                    ReadBlockTo((ulong)physical, buffer, _blockSize);
                }

                var writeLength = (int)Math.Min(remaining, _blockSize);
                output.Write(buffer, 0, writeLength);
                remaining -= writeLength;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Ext4Inode ReadInode(uint inodeNumber)
    {
        if (inodeNumber == 0 || inodeNumber > _superblock.InodesCount)
        {
            throw new FileSystemCorruptedException($"ext4 inode number {inodeNumber} is out of range (1..{_superblock.InodesCount}).");
        }

        var offset = checked(_inodeTableOffset + ((long)inodeNumber - 1) * _inodeSize);

        ReadExactlyAt(offset, _inodeBuffer);
        return Ext4Inode.FromBytes(_inodeBuffer, _inodeSize);
    }

    private byte[] ReadBlock(ulong physicalBlock)
    {
        if (_metadataBlockCache.TryGetValue(physicalBlock, out var entry))
        {
            _metadataBlockCache[physicalBlock] = (entry.Data, ++_cacheTick);
            return entry.Data;
        }

        var buffer = new byte[_blockSize];
        ReadBlockTo(physicalBlock, buffer, buffer.Length);
        if (_metadataBlockCache.Count >= MaxCachedMetadataBlocks)
        {
            EvictLeastRecentlyUsedMetadataBlock();
        }
        _metadataBlockCache[physicalBlock] = (buffer, ++_cacheTick);
        return buffer;
    }

    private void EvictLeastRecentlyUsedMetadataBlock()
    {
        var oldestStamp = long.MaxValue;
        ulong oldestKey = 0;
        foreach (var (key, value) in _metadataBlockCache)
        {
            if (value.Stamp < oldestStamp)
            {
                oldestStamp = value.Stamp;
                oldestKey = key;
            }
        }
        _metadataBlockCache.Remove(oldestKey);
    }

    private void ReadBlockTo(ulong physicalBlock, byte[] buffer, int length)
    {

        if (_journalOverlay.TryGetBlock((long)physicalBlock, out var overlayData))
        {
            overlayData.AsSpan(0, Math.Min(overlayData.Length, length)).CopyTo(buffer.AsSpan(0, length));
            return;
        }

        var offset = checked((long)physicalBlock * _blockSize);
        _readCancellation.ThrowIfCancellationRequested();
        if (offset < 0 || offset > _stream.Length || length > _stream.Length - offset)
        {
            throw new FileSystemCorruptedException(
                $"ext4 read range at {offset} of {length} bytes lies outside the image (length {_stream.Length}).");
        }

        _stream.Position = offset;
        _stream.ReadExactly(buffer.AsSpan(0, length));
    }

    private void ReadExactlyAt(long offset, byte[] buffer)
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (offset < 0 || offset > _stream.Length || buffer.Length > _stream.Length - offset)
        {
            throw new FileSystemCorruptedException(
                $"ext4 read range at {offset} of {buffer.Length} bytes lies outside the image (length {_stream.Length}).");
        }

        _stream.Position = offset;
        _stream.ReadExactly(buffer);
    }

    private long? MapBlock(Ext4Inode inode, uint logicalBlock)
    {
        if (inode.HasInlineData)
        {
            return null;
        }

        return inode.HasExtents
            ? Ext4Extent.Resolve(inode.BlockData, logicalBlock, ReadBlock)
            : MapIndirect(inode, logicalBlock);
    }

    private long? MapIndirect(Ext4Inode inode, uint logicalBlock)
    {
        var pointersPerBlock = _blockSize / 4;
        var pointers = inode.BlockPointers;
        if (logicalBlock < MaxExtentLeafPointers)
        {
            return pointers[logicalBlock] == 0 ? null : pointers[logicalBlock];
        }

        var x = (long)logicalBlock - MaxExtentLeafPointers;
        if (x < pointersPerBlock)
        {
            return pointers[12] == 0 ? null : ReadPointerBlock(pointers[12], (int)x);
        }

        x -= pointersPerBlock;
        var perDouble = (long)pointersPerBlock * pointersPerBlock;
        if (x < perDouble)
        {
            if (pointers[13] == 0)
            {
                return null;
            }

            var level1 = (int)(x / pointersPerBlock);
            var level2 = (int)(x % pointersPerBlock);
            var next1 = ReadPointerBlock(pointers[13], level1);
            if (next1 is null || next1 == 0)
            {
                return null;
            }

            return ReadPointerBlock(next1.Value, level2);
        }

        x -= perDouble;
        if (x >= perDouble * pointersPerBlock)
        {
            return null;
        }

        if (pointers[14] == 0)
        {
            return null;
        }

        var level1Double = (int)(x / perDouble);
        x %= perDouble;
        var nextDouble = ReadPointerBlock(pointers[14], level1Double);
        if (nextDouble is null || nextDouble == 0)
        {
            return null;
        }

        var next2 = ReadPointerBlock(nextDouble.Value, (int)(x / pointersPerBlock));
        if (next2 is null || next2 == 0)
        {
            return null;
        }

        return ReadPointerBlock(next2.Value, (int)(x % pointersPerBlock));
    }

    private uint? ReadPointerBlock(uint blockNumber, int index)
    {
        var data = ReadBlock(blockNumber);
        var address = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4, 4));
        return address == 0 ? null : address;
    }

    /// <summary>
    /// Replays the ext4 journal (JBD2) log and returns a dirty-block overlay.
    /// <para>重放 ext4 journal（JBD2）日志，返回脏块覆盖层。</para>
    /// </summary>
    private Journal.JournalOverlay ReplayJournal()
    {
        var journalIno = _superblock.JournalInum;
        if (journalIno == 0)
        {
            return Journal.JournalOverlay.Empty;
        }

        var journalInode = ReadInode(journalIno);
        var journalSize = journalInode.Size;
        if (journalSize <= 0)
        {
            return Journal.JournalOverlay.Empty;
        }

        var jsbBlock = new byte[_blockSize];
        var firstPhysical = MapBlock(journalInode, 0);
        if (firstPhysical is null)
        {
            return Journal.JournalOverlay.Empty;
        }

        ReadExactlyAt(checked((long)firstPhysical * _blockSize), jsbBlock);
        var jsb = Journal.JournalSuperblock.FromBytes(jsbBlock);
        if (jsb.IsEmpty || jsb.MaxLen == 0)
        {
            return Journal.JournalOverlay.Empty;
        }

        var journalBlocks = new Dictionary<uint, ulong>();
        for (uint i = 0; i <= jsb.MaxLen && i * _blockSize < journalSize; i++)
        {
            var phys = MapBlock(journalInode, i);
            if (phys is not null)
            {
                journalBlocks[i] = (ulong)phys;
            }
        }

        var transactions = Journal.Jbd2Parser.ScanTransactions(jsb, blockNr =>
        {
            if (!journalBlocks.TryGetValue(blockNr, out var physBlock))
            {
                return new byte[_blockSize];
            }

            var data = new byte[_blockSize];
            ReadExactlyAt(checked((long)physBlock * _blockSize), data);
            return data;
        });

        return Journal.TransactionReplayer.Replay(transactions);
    }
}