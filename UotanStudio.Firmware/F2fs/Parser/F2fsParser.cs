using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs 文件清单条目（路径 + 大小 + inode 号 + 类型）。
/// <para>An F2FS file-list entry (path + size + inode + kind).</para>
/// </summary>
public sealed record F2fsFileEntry(string Path, long Size, uint Inode, F2fsParser.F2fsEntryKind Kind, string? SymlinkTarget = null);

/// <summary>
/// 全托管 f2fs 镜像解析器：打开镜像（或流），校验超级块，解析 checkpoint 定位 NAT，
/// 从根 inode 遍历目录树，并经 inode 内 i_addr 直连/间接节点与 extent cache 读取文件内容，
/// 稀疏洞补零，支持 inline data 与符号链接。所有外部长度/偏移/块号均经 checked 验证。
/// <para>A fully managed F2FS image parser: opens an image (or stream), validates the superblock,
/// locates the NAT via the checkpoint, walks the directory tree from the root inode, and reads file
/// contents through the inode's direct/indirect nodes and extent cache with zero-filled sparse holes,
/// inline data and symlink support. All external lengths, offsets and block numbers are verified.</para>
/// </summary>
public sealed class F2fsParser : IDisposable
{
    /// <summary>
    /// Kind of a parsed filesystem entry.
    /// <para>已解析文件系统条目的类型。</para>
    /// </summary>
    public enum F2fsEntryKind
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

    /// <summary>单次载入内存的目录数据大小上限，防止损坏镜像造成过量分配。
    /// Upper bound for one in-memory directory component.</summary>
    private const long MaxDirectoryDataSize = 512L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly object _streamLock = new();
    private long _imageSize;
    private F2fsSuperblock _superblock = null!;
    private F2fsCheckpoint _checkpoint = null!;
    private int _blockSize;
    private int _blocksPerSeg;
    private readonly Dictionary<string, F2fsFileEntry> _entries = new(StringComparer.Ordinal);
    private bool _walked;

    /// <summary>
    /// Opens an f2fs image file for parsing.
    /// <para>打开 f2fs 镜像文件进行解析。</para>
    /// </summary>
    public F2fsParser(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        _stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Initialize();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Parses an f2fs image from an existing stream (the parser takes ownership and disposes it).
    /// <para>从既有流解析 f2fs 镜像（所有权转移，释放解析器时一并释放流）。</para>
    /// </summary>
    public F2fsParser(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        try
        {
            Initialize();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Total image size in bytes.
    /// <para>镜像总字节数。</para>
    /// </summary>
    public long ImageSize => _imageSize;

    /// <summary>
    /// Block size in bytes.
    /// <para>块大小（字节）。</para>
    /// </summary>
    public long BlockSize => _blockSize;

    /// <summary>
    /// Segment size in bytes.
    /// <para>段大小（字节）。</para>
    /// </summary>
    public long SegmentSize => (long)_blockSize * _blocksPerSeg;

    /// <summary>
    /// Total user block count (s_block_count).
    /// <para>用户块总数（s_block_count）。</para>
    /// </summary>
    public ulong BlockCount => _superblock.BlockCount;

    /// <summary>
    /// Total inode count reported by the checkpoint (valid_inode_count).
    /// <para>checkpoint 报告的 inode 总数（valid_inode_count）。</para>
    /// </summary>
    public uint InodeCount => _checkpoint.ValidInodeCount;

    /// <summary>
    /// Volume name (s_volume_name).
    /// <para>卷名（s_volume_name）。</para>
    /// </summary>
    public string VolumeName => _superblock.VolumeName;

    /// <summary>
    /// Root inode number (s_root_ino).
    /// <para>根 inode 号（s_root_ino）。</para>
    /// </summary>
    public uint RootIno => _superblock.RootIno;

    /// <summary>
    /// Number of non-directory entries found during the tree walk.
    /// <para>目录树遍历到的非目录条目数。</para>
    /// </summary>
    public int FileCount => _entries.Values.Count(static e => e.Kind != F2fsEntryKind.Directory);

    /// <summary>
    /// Number of directory entries found during the tree walk.
    /// <para>目录树遍历到的目录条目数。</para>
    /// </summary>
    public int DirectoryCount => _entries.Values.Count(static e => e.Kind == F2fsEntryKind.Directory);

    private void Initialize()
    {
        _imageSize = _stream.Length;
        if (_stream.Position != 0)
        {
            _stream.Position = 0;
        }

        _superblock = F2fsSuperblock.FromBytes(
            ReadBytes(F2fsSuperblock.SuperblockOffset, F2fsSuperblock.SuperblockSize, "Superblock"));
        _blockSize = _superblock.BlockSize;
        if (_blockSize != F2fsNode.NodeSize)
        {
            throw new InvalidDataException($"Unsupported f2fs block size {_blockSize} (only 4096 is supported).");
        }

        _blocksPerSeg = _superblock.BlocksPerSeg;
        if (_blocksPerSeg <= 0 || !IsPowerOfTwo(_blocksPerSeg))
        {
            throw new InvalidDataException($"Invalid f2fs blocks-per-segment {_blocksPerSeg}.");
        }

        // 校验各元数据区地址与段数自洽。Validate metadata area addresses and segment counts.
        if (_superblock.SegmentCount == 0
            || _superblock.Segment0Blkaddr == 0
            || _superblock.CpBlkaddr < _superblock.Segment0Blkaddr
            || _superblock.SitBlkaddr <= _superblock.CpBlkaddr
            || _superblock.NatBlkaddr <= _superblock.SitBlkaddr
            || _superblock.SsaBlkaddr <= _superblock.NatBlkaddr
            || _superblock.MainBlkaddr <= _superblock.SsaBlkaddr
            || _superblock.RootIno < F2fsNode.ReservedNodeCount)
        {
            throw new InvalidDataException("F2FS superblock metadata-area layout is inconsistent.");
        }

        // 按超级块声明的段数校验镜像总长：截断镜像（metadata/主区落在文件外）在构造期即拒绝，
        // 避免后续随机读取时以 EndOfStreamException 泄漏。Validate the total image length against
        // the declared segment counts so truncated images (metadata/main area beyond EOF) are
        // rejected at construction instead of leaking an EndOfStreamException on later reads.
        var declaredImageBlocks = checked((long)_superblock.SegmentCount * _blocksPerSeg + _superblock.Segment0Blkaddr);
        var declaredImageSize = checked(declaredImageBlocks * _blockSize);
        if (_imageSize < declaredImageSize)
        {
            throw new InvalidDataException(
                $"F2FS image is truncated: {_imageSize} bytes, but the superblock declares {declaredImageSize} bytes.");
        }

        _checkpoint = ReadCheckpoint();
        if (_checkpoint.ValidNodeCount == 0 && _checkpoint.ValidInodeCount == 0)
        {
            throw new InvalidDataException("F2FS checkpoint reports an empty filesystem.");
        }
    }

    /// <summary>
    /// Walks the directory tree from the root inode and returns all entries
    /// (files, directories and symlinks) with their relative paths.
    /// <para>从根 inode 遍历目录树，返回所有条目（文件、目录与符号链接）及其相对路径。</para>
    /// </summary>
    public IReadOnlyList<F2fsFileEntry> ListFiles()
    {
        if (!_walked)
        {
            var visited = new HashSet<uint>();
            WalkDirectory(_superblock.RootIno, string.Empty, visited);
            _walked = true;
        }

        return [.. _entries.Values];
    }

    /// <summary>
    /// Writes the full content of the file at <paramref name="path"/> to <paramref name="output"/>,
    /// zero-filling sparse holes. Symlinks yield their target bytes.
    /// <para>将 <paramref name="path"/> 处文件的完整内容写入 <paramref name="output"/>，
    /// 稀疏洞补零；符号链接输出其目标字节。</para>
    /// </summary>
    public void ReadFile(string path, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var entry = FindEntry(path);
        if (entry.Kind == F2fsEntryKind.Directory)
        {
            throw new InvalidOperationException($"Cannot read a directory as a file: '{path}'.");
        }

        var inode = ReadInode(entry.Inode);
        WriteFileData(entry.Inode, inode, output);
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
            if (entry.Kind == F2fsEntryKind.Directory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            if (entry.Kind == F2fsEntryKind.Symlink)
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
        GC.SuppressFinalize(this);
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private F2fsFileEntry FindEntry(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ListFiles();
        if (!_entries.TryGetValue(path, out var entry))
        {
            throw new FileNotFoundException($"No such file in f2fs image: '{path}'.", path);
        }

        return entry;
    }

    private void WalkDirectory(uint ino, string prefix, HashSet<uint> visited)
    {
        if (!visited.Add(ino))
        {
            throw new InvalidDataException($"Directory cycle detected at inode {ino}.");
        }

        var inode = ReadInode(ino);
        if (!inode.IsDirectory)
        {
            return;
        }

        foreach (var entry in ReadDirectory(ino, inode))
        {
            if (entry.Name is "." or ".." || entry.Name.Length == 0)
            {
                continue;
            }

            if (!IsSafeName(entry.Name))
            {
                continue;
            }

            var childInode = ReadInode(entry.Ino);
            var childPath = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            var kind = ClassifyKind(entry.FileType, childInode);
            var symlinkTarget = kind == F2fsEntryKind.Symlink ? ReadSymlinkTarget(entry.Ino, childInode) : null;
            _entries[childPath] = new F2fsFileEntry(childPath, (long)childInode.Size, entry.Ino, kind, symlinkTarget);
            if (kind == F2fsEntryKind.Directory)
            {
                WalkDirectory(entry.Ino, childPath, visited);
            }
        }
    }

    private static F2fsEntryKind ClassifyKind(byte fileType, F2fsInode inode)
        => fileType switch
        {
            F2fsDirectoryEntry.TypeRegularFile => F2fsEntryKind.File,
            F2fsDirectoryEntry.TypeDirectory => F2fsEntryKind.Directory,
            F2fsDirectoryEntry.TypeSymlink => F2fsEntryKind.Symlink,
            _ => inode.IsDirectory ? F2fsEntryKind.Directory
                : inode.IsSymlink ? F2fsEntryKind.Symlink
                : F2fsEntryKind.File,
        };

    private static bool IsSafeName(string name)
        => !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0') && name != "." && name != "..";

    private string? ReadSymlinkTarget(uint nid, F2fsInode inode)
    {
        using var ms = new MemoryStream();
        WriteFileData(nid, inode, ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private List<F2fsDirectoryEntry> ReadDirectory(uint ino, F2fsInode inode)
    {
        if (inode.HasInlineDentry)
        {
            // 内联目录：inline 区域即为目录数据区（尺寸按内联参数计算）。
            // Inline directory: the inline area is the directory data area (inline sizes).
            var inlineData = inode.MaxInlineData;
            var inlineSlots = inlineData * 8 / ((F2fsDirectoryEntry.DirectoryEntrySize + F2fsDirectoryEntry.SlotLength) * 8 + 1);
            var inlineBitmapSize = (inlineSlots + 7) / 8;
            var inlineReserved = inlineData - (F2fsDirectoryEntry.DirectoryEntrySize + F2fsDirectoryEntry.SlotLength) * inlineSlots - inlineBitmapSize;
            if (inlineSlots <= 0
                || inlineBitmapSize < 0
                || inlineReserved < 0
                || inode.InlineDataOffset < 0
                || inode.InlineDataOffset > F2fsNode.InodeEndOffset
                || inlineData < 0
                || inlineData > F2fsNode.InodeEndOffset - inode.InlineDataOffset)
            {
                throw new InvalidDataException("F2FS inline directory layout exceeds the inode bounds.");
            }

            var node = ReadInodeNode(ino);
            var area = node.AsSpan(inode.InlineDataOffset, inlineData);
            return F2fsDirectoryEntry.ParseArea(area, inlineBitmapSize, inlineReserved, inlineSlots, inlineData);
        }

        var dataLength = (long)inode.Size;
        if (inode.Size > (ulong)MaxDirectoryDataSize || dataLength < 0)
        {
            throw new InvalidDataException(
                $"Directory data size {inode.Size} exceeds the limit of {MaxDirectoryDataSize} bytes.");
        }

        var blockCount = (uint)((dataLength + _blockSize - 1) / _blockSize);

        var result = new List<F2fsDirectoryEntry>();
        var blockBuffer = new byte[_blockSize];
        var remaining = dataLength;
        for (var logical = 0u; logical < blockCount && remaining > 0; logical++)
        {
            var physical = MapBlock(inode, logical);
            blockBuffer.AsSpan().Clear();
            if (physical is not null)
            {
                ReadBlockTo((ulong)physical, blockBuffer);
            }

            var blockLength = (int)Math.Min(_blockSize, remaining);
            result.AddRange(F2fsDirectoryEntry.ParseArea(blockBuffer.AsSpan(0, blockLength), F2fsDirectoryEntry.BitmapSize, F2fsDirectoryEntry.ReservedSize, F2fsDirectoryEntry.SlotsPerBlock, F2fsNode.NodeSize));
            remaining -= blockLength;
        }

        return result;
    }

    private F2fsCheckpoint ReadCheckpoint()
    {
        var pack1 = ReadBlockChecked(_superblock.CpBlkaddr, "Checkpoint");
        var checkpoint = F2fsCheckpoint.FromBytes(pack1);
        if (checkpoint.CheckpointVer != 0)
        {
            return checkpoint;
        }

        var pack2Block = checked((long)_superblock.CpBlkaddr + _blocksPerSeg);
        var pack2 = ReadBlockChecked((ulong)pack2Block, "Checkpoint pack 2");
        return F2fsCheckpoint.FromBytes(pack2);
    }

    private F2fsInode ReadInode(uint nid)
    {
        var node = ReadInodeNode(nid);
        return F2fsInode.FromBytes(node);
    }

    private byte[] ReadInodeNode(uint nid)
    {
        var blockAddress = ReadNatBlockAddress(nid);
        var node = ReadBlockChecked(blockAddress, $"Inode nid {nid}");
        var footer = F2fsNodeFooter.FromBytes(node);
        if (!footer.IsInode)
        {
            throw new InvalidDataException($"Node nid {nid} is not an inode node (nid={footer.Nid}, ino={footer.Ino}).");
        }

        return node;
    }

    private uint ReadNatBlockAddress(uint nid)
    {
        if (nid < F2fsNode.ReservedNodeCount)
        {
            throw new InvalidDataException($"Invalid reserved nid {nid}.");
        }

        // 内核 current_nat_addr() 使用 nat_blkaddr + 2 * block_off（NAT 块按 2 块间隔存放，
        // 配合 checkpoint 版本位图；全零位图即读取该首副本）。
        // The kernel's current_nat_addr() reads nat_blkaddr + 2 * block_off (NAT blocks are
        // spaced 2 apart with the checkpoint version bitmap; an all-zero bitmap selects this copy).
        var natBlockIndex = nid / F2fsNode.NatEntryPerBlock;
        var natBlockAddress = checked(_superblock.NatBlkaddr + natBlockIndex * 2u);
        var natBlock = ReadBlockChecked(natBlockAddress, $"NAT block for nid {nid}");
        var entryOffset = checked((int)(nid % F2fsNode.NatEntryPerBlock) * F2fsNode.NatEntrySize);
        var blockAddress = BinaryPrimitives.ReadUInt32LittleEndian(natBlock.AsSpan(entryOffset + F2fsNode.NatEntryBlockOffset, 4));
        if (blockAddress == 0)
        {
            throw new InvalidDataException($"NAT entry for nid {nid} is missing.");
        }

        return blockAddress;
    }

    private long? MapBlock(F2fsInode inode, uint logicalBlock)
    {
        if (inode.Extent.Contains(logicalBlock))
        {
            return inode.Extent.Map(logicalBlock);
        }

        if (logicalBlock < F2fsNode.DefAddrsPerInode)
        {
            var direct = inode.Addr[logicalBlock];
            return direct == 0 ? null : direct;
        }

        var x = (long)logicalBlock - F2fsNode.DefAddrsPerInode;
        if (x < F2fsNode.AddrsPerBlock)
        {
            return ReadDnodeAddress(inode.Nid[0], (int)x, logicalBlock);
        }

        x -= F2fsNode.AddrsPerBlock;
        if (x < F2fsNode.AddrsPerBlock)
        {
            return ReadDnodeAddress(inode.Nid[1], (int)x, logicalBlock);
        }

        x -= F2fsNode.AddrsPerBlock;
        var perIndirect = (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock;
        if (x < perIndirect)
        {
            var level1 = (int)(x / F2fsNode.AddrsPerBlock);
            var level2 = (int)(x % F2fsNode.AddrsPerBlock);
            return ReadIndirectAddress(inode.Nid[2], level1, level2, logicalBlock);
        }

        x -= perIndirect;
        if (x < perIndirect)
        {
            var level1 = (int)(x / F2fsNode.AddrsPerBlock);
            var level2 = (int)(x % F2fsNode.AddrsPerBlock);
            return ReadIndirectAddress(inode.Nid[3], level1, level2, logicalBlock);
        }

        // 超出实现支持的寻址深度（double indirect 未实现）。
        // Beyond the supported mapping depth (double indirect is not implemented).
        return null;
    }

    private long? ReadDnodeAddress(uint nodeNid, int index, uint logicalBlock)
    {
        if (nodeNid == 0 || index < 0 || index >= F2fsNode.AddrsPerBlock)
        {
            return null;
        }

        var node = ReadBlockChecked(ReadNatBlockAddress(nodeNid), $"Direct node {nodeNid} (logical {logicalBlock})");
        var address = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(index * 4, 4));
        return address == 0 ? null : address;
    }

    private long? ReadIndirectAddress(uint indirectNid, int level1, int level2, uint logicalBlock)
    {
        if (indirectNid == 0 || level1 < 0 || level1 >= F2fsNode.AddrsPerBlock)
        {
            return null;
        }

        var node = ReadBlockChecked(ReadNatBlockAddress(indirectNid), $"Indirect node {indirectNid} (logical {logicalBlock})");
        var directNid = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(level1 * 4, 4));
        if (directNid == 0)
        {
            return null;
        }

        return ReadDnodeAddress(directNid, level2, logicalBlock);
    }

    private void WriteFileData(uint nid, F2fsInode inode, Stream output)
    {
        if (inode.Size == 0)
        {
            return;
        }

        if (inode.HasInlineData)
        {
            var node = ReadInodeNode(nid);
            var inline = inode.ReadInlineBytes(node, inode.Size);
            output.Write(inline);
            return;
        }

        var maxSupportedBlocks = F2fsNode.Indirect1Block + (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock;
        var maxSupportedSize = checked(maxSupportedBlocks * _blockSize);
        if (inode.Size > (ulong)maxSupportedSize)
        {
            throw new InvalidDataException("F2FS file data exceeds the supported block-mapping depth.");
        }

        var blockCount = (uint)(((long)inode.Size + _blockSize - 1) / _blockSize);
        var buffer = new byte[_blockSize];
        var remaining = (long)inode.Size;
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

    private byte[] ReadBlockChecked(ulong blockAddress, string what)
    {
        var offset = checked((long)blockAddress * _blockSize);
        return ReadBytes(offset, _blockSize, what);
    }

    private void ReadBlockTo(ulong blockAddress, byte[] buffer)
    {
        var offset = checked((long)blockAddress * _blockSize);
        lock (_streamLock)
        {
            _stream.Position = offset;
            _stream.ReadExactly(buffer);
        }
    }

    private byte[] ReadBytes(long offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || (long)length > _imageSize - offset)
        {
            throw new InvalidDataException($"{what} exceeds the image bounds.");
        }

        var buffer = new byte[length];
        lock (_streamLock)
        {
            _stream.Position = offset;
            _stream.ReadExactly(buffer);
        }

        return buffer;
    }
}