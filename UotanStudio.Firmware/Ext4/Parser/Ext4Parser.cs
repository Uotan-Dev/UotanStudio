using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Ext4.Core;

namespace UotanStudio.Firmware.Ext4;

/// <summary>
/// Fully managed ext4 image parser: opens an image (or stream), validates the superblock,
/// walks the directory tree from the root inode (2) and reads file contents through
/// extent/indirect block mapping with zero-filled sparse holes.
/// <para>全托管 ext4 镜像解析器：打开镜像（或流），校验超级块，从根 inode（2）
/// 遍历目录树，并通过 extent/间接块映射读取文件内容（稀疏洞补零）。</para>
/// </summary>
public sealed class Ext4Parser : IDisposable
{
    /// <summary>
    /// Kind of a parsed filesystem entry.
    /// <para>已解析文件系统条目的类型。</para>
    /// </summary>
    public enum Ext4EntryKind
    {
        /// <summary>
        /// Regular file.
        /// <para>普通文件。</para>
        /// </summary>
        File,

        /// <summary>
        /// Directory.
        /// <para>目录。</para>
        /// </summary>
        Directory,

        /// <summary>
        /// Symbolic link.
        /// <para>符号链接。</para>
        /// </summary>
        Symlink,
    }

    /// <summary>
    /// A filesystem entry: relative path, size, inode number and kind.
    /// <para>一个文件系统条目：相对路径、大小、inode 号与类型。</para>
    /// </summary>
    public sealed record Ext4FileEntry(string Path, long Size, uint Inode, Ext4EntryKind Kind, string? SymlinkTarget = null, IReadOnlyDictionary<string, byte[]>? Xattrs = null);

    private const int SuperblockReadLength = 1024 + 360;
    private const uint RootInodeNumber = 2;
    private const uint Ext4XattrMagic = 0xEA020000;
    private const int MaxExtentLeafPointers = 12; // 12 direct pointers in the indirect scheme
    private const long MaximumGroupDescriptorTableSize = 512L * 1024 * 1024;
    private const int MaxDirectoryDepth = 256; // guards against pathologically deep (or cyclic) trees

    /// <summary>
    /// EA_INODE incompatible feature flag (0x0400): large xattr values are stored in a
    /// dedicated inode rather than inline in the xattr block.
    /// <para>EA_INODE 不兼容特性标志（0x0400）：大型 xattr 值存于独立 inode 而非 xattr 块内。</para>
    /// </summary>
    private const uint FeatureIncompatEaInode = 0x0400;

    /// <summary>
    /// METADATA_CSUM read-only compatible feature flag (0x0400): when set, xattr blocks carry
    /// a 4-byte CRC32c at the very end of the block.
    /// <para>METADATA_CSUM 只读兼容特性标志（0x0400）：启用时 xattr 块末尾 4 字节为 CRC32c。</para>
    /// </summary>
    private const uint FeatureRoCompatMetadataCsum = 0x0400;

    /// <summary>
    /// Maximum xattr value size we are willing to read into memory (64 KiB).
    /// Larger values typically indicate an ea_inode reference.
    /// <para>我们愿意读入内存的最大 xattr 值大小（64 KiB）。更大的值通常指向 ea_inode 引用。</para>
    /// </summary>
    private const int MaxXattrValueSize = 64 * 1024;

    /// <summary>
    /// Xattr name-index prefix map. ext4 xattr entries store only the name suffix; the prefix
    /// is determined by the name_index byte.
    /// <para>Xattr name-index 前缀映射。ext4 xattr 条目仅存名称后缀，前缀由 name_index 字节决定。</para>
    /// </summary>
    private static readonly string[] XattrNameIndexPrefixes =
    [
        "",        // 0 — unused
        "user.",   // 1 — EXT4_XATTR_INDEX_USER
        "system.posix_acl_access", // 2 — EXT4_XATTR_INDEX_POSIX_ACL_ACCESS (full name, no suffix)
        "system.posix_acl_default", // 3 — EXT4_XATTR_INDEX_POSIX_ACL_DEFAULT (full name, no suffix)
        "trusted.", // 4 — EXT4_XATTR_INDEX_TRUSTED
        "",        // 5 — EXT4_XATTR_INDEX_LUSTRE (rare)
        "security.", // 6 — EXT4_XATTR_INDEX_SECURITY
        "",        // 7 — EXT4_XATTR_INDEX_RICHACL
        "system.",   // 8 — EXT4_XATTR_INDEX_SYSTEM
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
    private Journal.JournalOverlay _journalOverlay = Journal.JournalOverlay.Empty;

    /// <summary>
    /// Opens an ext4 image file for parsing.
    /// <para>打开 ext4 镜像文件进行解析。</para>
    /// </summary>
    public Ext4Parser(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        _stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Initialize();
    }

    /// <summary>
    /// Parses an ext4 image from an existing stream (ownership is transferred).
    /// <para>从既有流解析 ext4 镜像（所有权转移）。</para>
    /// </summary>
    public Ext4Parser(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        Initialize();
    }

    /// <summary>
    /// Total partition size in bytes (block count × block size).
    /// <para>分区总大小（字节，块数 × 块大小）。</para>
    /// </summary>
    public long PartitionSize => checked((long)_superblock.BlocksCount * _blockSize);

    /// <summary>
    /// Block size in bytes.
    /// <para>块大小（字节）。</para>
    /// </summary>
    public long BlockSize => _blockSize;

    /// <summary>
    /// Total number of blocks in the filesystem.
    /// <para>文件系统中的块总数。</para>
    /// </summary>
    public ulong BlockCount => _superblock.BlocksCount;

    /// <summary>
    /// Total number of inodes in the filesystem.
    /// <para>文件系统中的 inode 总数。</para>
    /// </summary>
    public uint InodeCount => _superblock.InodesCount;

    /// <summary>
    /// Number of non-directory entries found during the tree walk.
    /// <para>目录树遍历到的非目录条目数。</para>
    /// </summary>
    public int FileCount => _entries.Values.Count(e => e.Kind != Ext4EntryKind.Directory);

    /// <summary>
    /// Number of directory entries found during the tree walk.
    /// <para>目录树遍历到的目录条目数。</para>
    /// </summary>
    public int DirectoryCount => _entries.Values.Count(e => e.Kind == Ext4EntryKind.Directory);

    private void Initialize()
    {
        _stream.Position = 0;
        var prefix = new byte[SuperblockReadLength];
        _stream.ReadExactly(prefix);
        _superblock = Ext4Superblock.FromBytes(prefix);
        _blockSize = checked((int)_superblock.BlockSize);
        if (_blockSize is not (1024 or 2048 or 4096 or 8192 or 16384 or 32768 or 65536))
        {
            throw new InvalidDataException($"Unsupported ext4 block size {_blockSize}.");
        }

        // 校验不兼容特性标志：对已知不支持的位抛出异常。
        // Validate incompatible feature flags: throw on unsupported bits.
        const uint supportedIncompat = 0x0001 /* EXTENTS */ | 0x0040 /* 64BIT */ | 0x0080 /* MMP */ | 0x0200 /* FLEX_BG */ | 0x0400 /* EA_INODE */;
        _ = 0x0001 /* SPARSE_SUPER */ | 0x0002 /* LARGE_FILE */ | 0x0400 /* METADATA_CSUM */; // supported_ro_compat
        _ = 0x0004 /* HAS_JOURNAL */ | 0x0020 /* DIR_INDEX */; // supported_compat

        var unsupportedIncompat = _superblock.FeatureIncompat & ~supportedIncompat;
        if (unsupportedIncompat != 0)
        {
            throw new InvalidDataException($"ext4 image has unsupported incompatible features: 0x{unsupportedIncompat:X8}.");
        }

        _inodeSize = _superblock.InodeSize == 0 ? 256 : _superblock.InodeSize;
        _inodeBuffer = new byte[_inodeSize];
        var descSize = _superblock.EffectiveDescSize;
        var groupCount = checked((int)((_superblock.BlocksCount + _superblock.BlocksPerGroup - 1) / _superblock.BlocksPerGroup));
        if (groupCount <= 0)
        {
            throw new InvalidDataException("ext4 image has no block groups.");
        }

        _groups = new Ext4BlockGroup[groupCount];
        var gdtLength = checked((long)groupCount * descSize);
        // The primary group descriptor table starts in the block right after the superblock
        // block: (s_first_data_block + 1) * block_size (block 2 for 1 KiB, block 1 otherwise).
        // 主块组描述符表起始于超级块所在块之后的块：(s_first_data_block + 1) * 块大小
        //（1 KiB 块为块 2，其余为块 1）。
        var gdtOffset = checked((_superblock.FirstDataBlock + 1) * _blockSize);
        if (gdtLength > MaximumGroupDescriptorTableSize || gdtLength > _stream.Length - gdtOffset)
        {
            throw new InvalidDataException("ext4 group descriptor table exceeds the image bounds or supported metadata limit.");
        }
        var gdtBytes = new byte[checked((int)gdtLength)];
        _stream.Position = gdtOffset;
        _stream.ReadExactly(gdtBytes);
        for (var i = 0; i < groupCount; i++)
        {
            _groups[i] = Ext4BlockGroup.FromBytes(gdtBytes.AsSpan(i * descSize, descSize), descSize);
        }

        _inodeTableOffset = checked((long)_groups[0].InodeTable * _blockSize);
        if (_inodeTableOffset < 0 || _inodeTableOffset >= _stream.Length)
        {
            throw new InvalidDataException("ext4 inode table lies outside the image bounds.");
        }

        // Journal 重放：当文件系统有日志且需要恢复时，重放已提交事务到覆盖层。
        // Journal replay: when the filesystem has a journal and needs recovery, replay committed transactions.
        if ((_superblock.FeatureCompat & 0x4) != 0 && (_superblock.FeatureIncompat & 0x4) != 0)
        {
            try
            {
                _journalOverlay = ReplayJournal();
            }
            catch (Exception ex)
            {
                // Journal 重放失败不影响基本解析（可能日志为空或损坏）。
                // Journal replay failure does not affect basic parsing (journal may be empty or corrupt).
                System.Diagnostics.Debug.WriteLine($"ext4 journal replay failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Walks the directory tree from the root inode (2) and returns all entries
    /// (files, directories and symlinks) with their relative paths.
    /// <para>从根 inode（2）遍历目录树，返回所有条目（文件、目录与符号链接）及其相对路径。</para>
    /// </summary>
    public IReadOnlyList<Ext4FileEntry> ListFiles()
    {
        if (!_walked)
        {
            WalkDirectory(RootInodeNumber, string.Empty, new HashSet<uint>(), 0);
            _walked = true;
        }

        return [.. _entries.Values];
    }

    /// <summary>
    /// Writes the full content of the file at <paramref name="path"/> to
    /// <paramref name="output"/>, zero-filling sparse holes. Symlinks yield their target bytes.
    /// <para>将 <paramref name="path"/> 处文件的完整内容写入 <paramref name="output"/>，
    /// 稀疏洞补零；符号链接输出其目标字节。</para>
    /// </summary>
    public void ReadFile(string path, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var entry = FindEntry(path);

        if (entry.Kind == Ext4EntryKind.Directory)
        {
            throw new InvalidOperationException($"Cannot read a directory as a file: '{path}'.");
        }

        if (entry.Kind == Ext4EntryKind.Symlink)
        {
            var symlinkInode = ReadInode(entry.Inode);
            var targetBytes = System.Text.Encoding.UTF8.GetBytes(ReadSymlinkTarget(symlinkInode) ?? string.Empty);
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

        var blockCount = (uint)((entry.Size + _blockSize - 1) / _blockSize);
        var buffer = new byte[_blockSize];
        var remaining = entry.Size;
        for (var logical = 0u; logical < blockCount; logical++)
        {
            var physical = MapBlock(inode, logical);
            buffer.AsSpan().Clear();
            if (physical is not null)
            {
                ReadBlockTo((ulong)physical, buffer);
            }

            var writeLength = (int)Math.Min(remaining, _blockSize);
            output.Write(buffer, 0, writeLength);
            remaining -= writeLength;
        }
    }

    /// <summary>
    /// Extracts the whole filesystem tree into <paramref name="outputDirectory"/>.
    /// Symlinks are created as native symbolic links (falling back to a target-text file).
    /// <para>将整个文件系统树提取到 <paramref name="outputDirectory"/>。
    /// 符号链接以原生符号链接创建（失败时回退为目标文本文件）。</para>
    /// </summary>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        foreach (var entry in ListFiles())
        {
            var targetPath = Path.Combine(outputDirectory, entry.Path);
            if (entry.Kind == Ext4EntryKind.Directory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            if (entry.Kind == Ext4EntryKind.Symlink)
            {
                if (overwrite && (File.Exists(targetPath) || Directory.Exists(targetPath)))
                {
                    File.Delete(targetPath);
                }

                if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                {
                    try
                    {
                        File.CreateSymbolicLink(targetPath, entry.SymlinkTarget ?? string.Empty);
                    }
                    catch
                    {
                        System.IO.File.WriteAllText(targetPath, entry.SymlinkTarget ?? string.Empty);
                    }
                }

                continue;
            }

            if (File.Exists(targetPath) && !overwrite)
            {
                continue;
            }

            using (var output = File.Create(targetPath))
            {
                ReadFile(entry.Path, output);
            }
        }
    }

    /// <inheritdoc />
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
        // NOTE: Directories with EXT4_INDEX_FL (0x1000) use an htree (hashed b-tree) index
        // for fast name lookups. We do not decode the htree structure; instead we fall back
        // to a linear scan of every directory block, which is always correct because the
        // leaf blocks still contain the full ext4_dir_entry_2 records.
        // 注意：设置了 EXT4_INDEX_FL（0x1000）的目录使用 htree（哈希 B 树）索引加速名称查找。
        // 我们不解码 htree 结构，而是回退到逐块线性扫描目录项，因为叶块仍包含完整的
        // ext4_dir_entry_2 记录，线性扫描结果始终正确。

        if (depth > MaxDirectoryDepth)
        {
            throw new InvalidDataException($"ext4 directory tree exceeds the maximum depth of {MaxDirectoryDepth}.");
        }

        if (!visitedDirectories.Add(dirInode))
        {
            // ext4 directories cannot be hardlinked; revisiting one means the image is corrupt.
            // ext4 目录不允许硬链接；重复访问说明镜像损坏。
            throw new InvalidDataException($"ext4 directory cycle detected at inode {dirInode}.");
        }

        var inode = ReadInode(dirInode);
        var blockCount = (uint)((inode.Size + _blockSize - 1) / _blockSize);
        for (var logical = 0u; logical < blockCount; logical++)
        {
            var physical = MapBlock(inode, logical);
            if (physical is null)
            {
                continue;
            }

            var block = ReadBlock((ulong)physical);
            foreach (var entry in Ext4DirectoryEntry.Enumerate(block))
            {
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

                var child = ReadInode(entry.Inode);
                var childXattrs = MergeXattrs(
                    ParseInlineXattrs(_inodeBuffer, _inodeSize),
                    ReadExternalXattrBlock(child));
                var childPath = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                var kind = ClassifyKind(entry.FileType, child);
                var symlinkTarget = kind == Ext4EntryKind.Symlink ? ReadSymlinkTarget(child) : null;

                _entries[childPath] = new Ext4FileEntry(childPath, child.Size, entry.Inode, kind, symlinkTarget, childXattrs);
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
        => !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0') && name != "." && name != "..";

    /// <summary>
    /// Parses inline extended attributes (xattrs) from an inode's extra space.
    /// Returns <see langword="null"/> when no inline xattrs are present.
    /// <para>从 inode 的额外空间解析内联扩展属性（xattr）。无内联 xattr 时返回
    /// <see langword="null"/>。</para>
    /// </summary>
    private static Dictionary<string, byte[]>? ParseInlineXattrs(byte[] inodeRaw, int inodeSize)
    {
        // The core inode is 128 bytes; extended fields start at offset 128.
        // i_extra_isize (at 128) tells how many bytes the extended inode fields occupy;
        // the xattr ibody header sits immediately after them.
        // 核心 inode 为 128 字节；扩展字段从偏移 128 开始。
        // i_extra_isize（偏移 128）指示扩展 inode 字段所占字节数；
        // xattr ibody 头紧随其后。
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
        if (magic != Ext4XattrMagic)
        {
            return null;
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Entries follow the 4-byte ibody header.
        // 目录项紧跟在 4 字节 ibody 头之后。
        var pos = xattrStart + 4;
        while (pos + 12 <= inodeSize)
        {
            var nameLen = inodeRaw[pos];
            if (nameLen == 0)
            {
                break; // end sentinel / 结束标记
            }

            var valueOffs = BinaryPrimitives.ReadUInt16LittleEndian(inodeRaw.AsSpan(pos + 2, 2));
            var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeRaw.AsSpan(pos + 4, 4));
            var nameStart = pos + 12;
            var nameEnd = nameStart + nameLen;
            if (nameEnd > inodeSize)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(inodeRaw, nameStart, nameLen);
            var absValueOffset = xattrStart + valueOffs;
            if (absValueOffset >= xattrStart && absValueOffset + (int)valueSize <= inodeSize)
            {
                result[name] = inodeRaw.AsSpan(absValueOffset, (int)valueSize).ToArray();
            }

            // Advance to the next entry: 12-byte header + name, rounded up to 4 bytes.
            // 前进到下一目录项：12 字节头 + 名称，向上取整到 4 字节。
            pos += (12 + nameLen + 3) & ~3;
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Merges inline and external xattr dictionaries into a single read-only result.
    /// External xattrs take precedence over inline ones with the same key.
    /// Returns <see langword="null"/> when both inputs are null.
    /// <para>将内联与外部 xattr 字典合并为单一只读结果。同键时外部 xattr 优先。
    /// 两个输入均为 null 时返回 <see langword="null"/>。</para>
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
                merged[kv.Key] = kv.Value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Reads the external extended attribute block pointed to by the inode's
    /// <see cref="Ext4Inode.FileAcl"/> field. Returns <see langword="null"/> when the inode
    /// has no external xattr block. Handles <c>ea_inode</c> large-value references when the
    /// EA_INODE feature is enabled.
    /// <para>读取由 inode 的 <see cref="Ext4Inode.FileAcl"/> 字段指向的外部扩展属性块。
    /// 无外部 xattr 块时返回 <see langword="null"/>。启用 EA_INODE 特性时处理
    /// <c>ea_inode</c> 大值引用。</para>
    /// </summary>
    private Dictionary<string, byte[]>? ReadExternalXattrBlock(Ext4Inode inode)
    {
        if (inode.FileAcl == 0)
        {
            return null;
        }

        // The xattr block is a single filesystem block (≤ 64 KiB), safe to read whole.
        // xattr 块为单个文件系统块（≤ 64 KiB），整块读取安全。
        var block = ReadBlock(inode.FileAcl);

        // Validate the xattr block magic (0xEA020000 at offset 0, LE).
        // 校验 xattr 块魔数（偏移 0 处 0xEA020000，小端）。
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0, 4));
        if (magic != Ext4XattrMagic)
        {
            return null;
        }

        // When METADATA_CSUM is in use, the last 4 bytes of the block are a CRC32c;
        // the value region ends just before it. Otherwise values extend to the block end.
        // 启用 METADATA_CSUM 时块尾 4 字节为 CRC32c，值区域止于其前；
        // 否则值区域延伸至块尾。
        var hasCsum = (_superblock.FeatureRoCompat & FeatureRoCompatMetadataCsum) != 0;
        var valueRegionEnd = hasCsum ? block.Length - 4 : block.Length;

        // Entries start at offset 4 and grow forward; values grow backward from valueRegionEnd.
        // 目录项从偏移 4 开始向前生长；值从 valueRegionEnd 开始向后生长。
        var eaInodeEnabled = (_superblock.FeatureIncompat & FeatureIncompatEaInode) != 0;
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pos = 4;
        while (pos + 12 <= block.Length)
        {
            var nameLen = block[pos];
            if (nameLen == 0)
            {
                break; // end sentinel / 结束标记
            }

            var nameIndex = block[pos + 1];
            var valueOffs = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(pos + 2, 2));
            var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(pos + 4, 4));
            var nameStart = pos + 12;
            var nameEnd = nameStart + nameLen;
            if (nameEnd > block.Length)
            {
                break;
            }

            var suffix = Encoding.UTF8.GetString(block, nameStart, nameLen);
            var fullName = ResolveXattrName(nameIndex, suffix);

            // Determine the value: either inline in the xattr block (valueOffs relative to
            // block start) or, when value_size is too large for the block and the EA_INODE
            // feature is on, stored in a dedicated ea_inode whose number is valueOffs.
            // 确定值：或在 xattr 块内联（valueOffs 相对块起始），或当 value_size 过大且
            // 启用 EA_INODE 特性时存于专用 ea_inode（其 inode 号为 valueOffs）。
            byte[]? value = null;
            if (eaInodeEnabled && valueSize > (uint)block.Length)
            {
                // ea_inode reference: valueOffs holds the inode number of the value inode.
                // ea_inode 引用：valueOffs 持有值 inode 的 inode 号。
                value = ReadEaInodeValue(valueOffs);
            }
            else if (valueOffs != 0 && valueOffs < valueRegionEnd
                     && valueOffs + (int)valueSize <= valueRegionEnd)
            {
                value = block.AsSpan(valueOffs, (int)valueSize).ToArray();
            }

            if (value is not null && fullName.Length > 0)
            {
                result[fullName] = value;
            }

            // Advance: 12-byte header + name, rounded up to 4 bytes.
            // 前进：12 字节头 + 名称，向上取整到 4 字节。
            pos += (12 + nameLen + 3) & ~3;
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Builds the full xattr name from the name_index byte and the entry name suffix.
    /// For ACL entries (indices 2 and 3) the prefix is the full name and the suffix is
    /// ignored. For unknown indices the raw suffix is returned.
    /// <para>根据 name_index 字节与条目名称后缀构建完整 xattr 名称。
    /// ACL 条目（索引 2 与 3）前缀即为完整名称且忽略后缀。
    /// 未知索引时返回原始后缀。</para>
    /// </summary>
    private static string ResolveXattrName(byte nameIndex, string suffix)
    {
        if (nameIndex < XattrNameIndexPrefixes.Length)
        {
            var prefix = XattrNameIndexPrefixes[nameIndex];
            // POSIX ACL entries (2, 3) are full names — no suffix appended.
            // POSIX ACL 条目（2, 3）为完整名称——不附加后缀。
            if (nameIndex is 2 or 3)
            {
                return prefix;
            }

            return prefix + suffix;
        }

        return suffix;
    }

    /// <summary>
    /// Reads the value data from a dedicated <c>ea_inode</c> when a large xattr value is
    /// stored in its own inode. The value size is capped at <see cref="MaxXattrValueSize"/>.
    /// <para>当大型 xattr 值存于其自身 inode 时，从专用 <c>ea_inode</c> 读取值数据。
    /// 值大小上限为 <see cref="MaxXattrValueSize"/>。</para>
    /// </summary>
    private byte[]? ReadEaInodeValue(uint eaInodeNumber)
    {
        if (eaInodeNumber == 0)
        {
            return null;
        }

        var eaInode = ReadInode(eaInodeNumber);
        var size = (int)Math.Min(eaInode.Size, MaxXattrValueSize);
        if (size <= 0)
        {
            return [];
        }

        using var ms = new MemoryStream(size);
        ReadInodeContent(eaInode, size, ms);
        return ms.ToArray();
    }

    private string? ReadSymlinkTarget(Ext4Inode inode)
    {
        if (inode.IsSymlink && !inode.HasExtents && inode.Size < 60)
        {
            // Fast symlink: target stored directly in i_block.
            // 快速符号链接：目标直接存储在 i_block 中。
            return System.Text.Encoding.UTF8.GetString(inode.BlockData, 0, (int)inode.Size);
        }

        // Slow symlink: target read through the block mapping like a regular file.
        // 慢速符号链接：目标如同普通文件一样经块映射读取。
        using var ms = new MemoryStream();
        ReadInodeContent(inode, inode.Size, ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void ReadInodeContent(Ext4Inode inode, long size, Stream output)
    {
        if (inode.HasInlineData)
        {
            var length = (int)Math.Min(size, inode.InlineData.Length);
            if (length > 0)
            {
                output.Write(inode.InlineData, 0, length);
            }

            return;
        }

        var blockCount = (uint)((size + _blockSize - 1) / _blockSize);
        var buffer = new byte[_blockSize];
        var remaining = size;
        for (var logical = 0u; logical < blockCount; logical++)
        {
            var physical = MapBlock(inode, logical);
            buffer.AsSpan().Clear();
            if (physical is not null)
            {
                ReadBlockTo((ulong)physical, buffer);
            }

            var writeLength = (int)Math.Min(remaining, _blockSize);
            output.Write(buffer, 0, writeLength);
            remaining -= writeLength;
        }
    }

    private Ext4Inode ReadInode(uint inodeNumber)
    {
        if (inodeNumber == 0 || inodeNumber > _superblock.InodesCount)
        {
            throw new InvalidDataException($"ext4 inode number {inodeNumber} is out of range (1..{_superblock.InodesCount}).");
        }

        var offset = checked(_inodeTableOffset + ((long)inodeNumber - 1) * _inodeSize);
        // The shared buffer is safe because Ext4Inode.FromBytes copies every field out of it.
        // 共享缓冲是安全的，因为 Ext4Inode.FromBytes 会拷贝其中所有字段。
        ReadExactlyAt(offset, _inodeBuffer);
        return Ext4Inode.FromBytes(_inodeBuffer, _inodeSize);
    }

    private byte[] ReadBlock(ulong physicalBlock)
    {
        var buffer = new byte[_blockSize];
        ReadBlockTo(physicalBlock, buffer);
        return buffer;
    }

    private void ReadBlockTo(ulong physicalBlock, byte[] buffer)
    {
        // 优先使用 journal overlay 中的脏块数据。
        // Prefer dirty block data from the journal overlay.
        if (_journalOverlay.TryGetBlock((long)physicalBlock, out var overlayData))
        {
            overlayData.AsSpan(0, Math.Min(overlayData.Length, buffer.Length)).CopyTo(buffer);
            return;
        }

        ReadExactlyAt(checked((long)physicalBlock * _blockSize), buffer);
    }

    private void ReadExactlyAt(long offset, byte[] buffer)
    {
        if (offset < 0 || offset > _stream.Length || buffer.Length > _stream.Length - offset)
        {
            throw new InvalidDataException(
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
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4, 4));
    }

    /// <summary>
    /// 重放 ext4 journal（JBD2）日志，返回脏块覆盖层。
    /// <para>Replays the ext4 journal (JBD2) log and returns a dirty-block overlay.</para>
    /// </summary>
    private Journal.JournalOverlay ReplayJournal()
    {
        var journalIno = _superblock.JournalInum;
        if (journalIno == 0)
        {
            return Journal.JournalOverlay.Empty;
        }

        // 读取 journal inode 的数据块映射。
        // Read the journal inode's data block mappings.
        var journalInode = ReadInode(journalIno);
        var journalSize = journalInode.Size;
        if (journalSize <= 0)
        {
            return Journal.JournalOverlay.Empty;
        }

        // 读取 journal 超级块（第一个块）。
        // Read the journal superblock (first block).
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

        // 构建 journal 块号 → 物理块号的映射。
        // Build journal block number → physical block number mapping.
        var journalBlocks = new Dictionary<uint, ulong>();
        for (uint i = 0; i <= jsb.MaxLen && i * _blockSize < journalSize; i++)
        {
            var phys = MapBlock(journalInode, i);
            if (phys is not null)
            {
                journalBlocks[i] = (ulong)phys;
            }
        }

        // 扫描并重放事务。
        // Scan and replay transactions.
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