using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;

public sealed record F2fsFileEntry(string Path, long Size, uint Inode, F2fsParser.F2fsEntryKind Kind, string? SymlinkTarget = null, uint Uid = 0, uint Gid = 0, ushort Mode = 0, IReadOnlyDictionary<string, byte[]>? Xattrs = null, uint DeviceId = 0, uint Flags = 0);

public sealed class F2fsParser : IDisposable
{
    public enum F2fsEntryKind
    {
        File,

        Directory,

        Symlink,
    }

    private const long MaxDirectoryDataSize = 512L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly object _streamLock = new();
    private long _imageSize;
    private F2fsSuperblock _superblock = null!;
    private F2fsCheckpoint _checkpoint = null!;
    private ulong _checkpointBlock;
    private byte[] _natVersionBitmap = [];
    private readonly Dictionary<uint, uint> _natJournal = [];
    private int _blockSize;
    private int _blocksPerSeg;
    private readonly Dictionary<string, F2fsFileEntry> _entries = new(StringComparer.Ordinal);
    private bool _walked;

    /// <summary>
    /// Bounded cache of NAT blocks keyed by NAT block index; sequential file reads resolve
    /// many nids that share the same NAT block. Cleared wholesale when over capacity.
    /// <para>按 NAT 块号缓存 NAT 块的有界缓存——顺序读取大文件时大量 nid 共享同一 NAT 块；
    /// 超容量时整体清空。</para>
    /// </summary>
    private readonly Dictionary<ulong, byte[]> _natBlockCache = new();

    /// <summary>
    /// Bounded cache of node blocks keyed by nid (inode/direct/indirect nodes revisited per
    /// logical block during sequential reads). Guarded by <see cref="_streamLock"/> since node
    /// reads share the stream lock. Cleared wholesale when over capacity.
    /// <para>按 nid 缓存节点块（inode/direct/indirect）的有界缓存——顺序读取时每逻辑块都会
    /// 重走映射链；受 <see cref="_streamLock"/> 保护，超容量时整体清空。</para>
    /// </summary>
    private readonly Dictionary<uint, byte[]> _nodeBlockCache = new();

    /// <summary>
    /// Maximum number of blocks retained in each parser-level block cache.
    /// <para>解析器级块缓存各自保留的块数量上限。</para>
    /// </summary>
    private const int MaxCachedBlocks = 256;

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

    public long ImageSize => _imageSize;

    internal byte[] SourceUuid => _superblock.Uuid;
    internal string SourceVolumeName => _superblock.VolumeName;

    public long BlockSize => _blockSize;

    public long SegmentSize => (long)_blockSize * _blocksPerSeg;

    public ulong BlockCount => _superblock.BlockCount;

    public uint InodeCount => _checkpoint.ValidInodeCount;

    public string VolumeName => _superblock.VolumeName;

    public uint RootIno => _superblock.RootIno;

    public int FileCount => _entries.Values.Count(static e => e.Kind != F2fsEntryKind.Directory);

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


        var declaredImageBlocks = checked(_superblock.SegmentCount * _blocksPerSeg + _superblock.Segment0Blkaddr);
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


        ValidateSuperblockChecksum();
    }

    private const uint CrcPolynomial = 0xEDB88320;

    private void ValidateSuperblockChecksum()
    {
        var raw = ReadBytes(F2fsSuperblock.SuperblockOffset, F2fsSuperblock.SuperblockSize, "Superblock CRC");
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(F2fsSuperblock.SuperblockSize - 4, 4));
        if (storedCrc == 0)
        {
            return;
        }

        var computed = ComputeCrc32(F2fsSuperblock.Magic, raw.AsSpan(0, F2fsSuperblock.SuperblockSize - 4));
        if (computed != storedCrc)
        {
            throw new InvalidDataException($"F2FS superblock CRC mismatch: stored=0x{storedCrc:X8}, computed=0x{computed:X8}.");
        }
    }

    private static uint ComputeCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? CrcPolynomial : 0);
            }
        }

        return crc;
    }

    /// <summary>
    /// Walks the directory tree from the root inode and returns all entries (files, directories and symlinks) with their relative paths.
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

    /// <summary>Includes root inode metadata without changing the file-browser listing contract.</summary>
    public IReadOnlyList<F2fsFileEntry> ListEntriesWithRoot()
    {
        var entries = ListFiles();
        var root = ReadInode(_superblock.RootIno);
        return [new(".", (long)root.Size, _superblock.RootIno, F2fsEntryKind.Directory, Uid: root.Uid, Gid: root.Gid, Mode: root.Mode, Xattrs: ReadXattrs(_superblock.RootIno, root), Flags: root.Flags), .. entries];
    }

    /// <summary>
    /// Writes the full content of the file at path to output, zero-filling sparse holes.
    /// <para>将 path 处文件的完整内容写入 output， 稀疏洞补零；符号链接输出其目标字节。</para>
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
    /// Extracts the whole filesystem tree into outputDirectory.
    /// <para>将整个文件系统树提取到 outputDirectory。</para>
    /// </summary>
    public void ExtractToDirectory(string outputDirectory, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
        => ExtractToDirectory(outputDirectory, null, CancellationToken.None, overwrite, progress);

    /// <summary>Extracts with measured output bytes and cancellation, including writes within a large file.</summary>
    public void ExtractToDirectory(string outputDirectory, IProgress<(long Processed, long Total)>? byteProgress,
        CancellationToken cancellationToken, bool overwrite = true, IProgress<(int processed, int total)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var entries = ListFiles();
        var totalCount = entries.Count;
        var hostPaths = new UotanStudio.Firmware.BoundedContexts.FileSystem.Extraction.PortableExtractionPaths(outputDirectory, entries.Select(entry => entry.Path));
        var totalBytes = entries.Where(e => e.Kind != F2fsEntryKind.Directory
                && (e.Kind == F2fsEntryKind.Symlink || overwrite || !File.Exists(hostPaths.Resolve(e.Path))))
            .Sum(e => e.Kind == F2fsEntryKind.Symlink ? Encoding.UTF8.GetByteCount(e.SymlinkTarget ?? string.Empty)
                : (e.Mode & 0xF000) == 0x8000 ? e.Size : 0L);
        var bytes = new SharedKernel.ExtractionByteCounter(totalBytes, byteProgress, cancellationToken);
        var processedCount = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = hostPaths.Resolve(entry.Path);
            if (entry.Kind == F2fsEntryKind.Directory)
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

            if (entry.Kind == F2fsEntryKind.Symlink)
            {
                // The host file is a portable projection; the inode type and original image remain authoritative.
                SharedKernel.FileExtraction.WriteAtomic(targetPath,
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

            SharedKernel.FileExtraction.WriteAtomic(targetPath, output => { using var counted = bytes.Wrap(output); ReadFile(entry.Path, counted); }, overwrite);

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
            _entries[childPath] = new F2fsFileEntry(childPath, (long)childInode.Size, entry.Ino, kind, symlinkTarget, childInode.Uid, childInode.Gid, childInode.Mode, ReadXattrs(entry.Ino, childInode), childInode.Addr[childInode.ExtraIsizeWords] != 0 ? childInode.Addr[childInode.ExtraIsizeWords] : childInode.Addr[childInode.ExtraIsizeWords + 1], childInode.Flags);
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
        => !name.Contains('/') && !name.Contains('\0') && name != "." && name != "..";

    private string? ReadSymlinkTarget(uint nid, F2fsInode inode)
    {
        using var ms = new MemoryStream();
        WriteFileData(nid, inode, ms);
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private List<F2fsDirectoryEntry> ReadDirectory(uint ino, F2fsInode inode)
    {
        if (inode.HasInlineDentry)
        {


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
        if (inode.Size > MaxDirectoryDataSize || dataLength < 0)
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
        var first = TryReadCheckpointPack(_superblock.CpBlkaddr);
        var secondBlock = checked((ulong)_superblock.CpBlkaddr + (uint)_blocksPerSeg);
        var second = TryReadCheckpointPack(secondBlock);
        var useSecond = second is not null && (first is null || unchecked((long)(second.CheckpointVer - first.CheckpointVer)) > 0);
        var checkpoint = (useSecond ? second : first) ?? throw new InvalidDataException("Neither F2FS checkpoint pack is valid.");
        _checkpointBlock = useSecond ? secondBlock : _superblock.CpBlkaddr;
        if (_superblock.CpPayload >= _blocksPerSeg - 2 || checkpoint.CpPackStartSum <= _superblock.CpPayload
            || checkpoint.CpPackStartSum >= checkpoint.CpPackTotalBlockCount - 1)
            throw new InvalidDataException("Invalid F2FS checkpoint payload or summary range.");
        var bitmapOffset = checkpoint.VersionBitmapOffset;
        if ((checkpoint.CkptFlags & 0x400) != 0) bitmapOffset += 4;
        else if (_superblock.CpPayload == 0) bitmapOffset = checked(bitmapOffset + (int)checkpoint.SitVerBitmapBytesize);
        var payloadBytes = checked((long)(_superblock.CpPayload + 1) * _blockSize);
        var expectedNatBytes = checked((long)_superblock.SegmentCountNat / 2 * _blocksPerSeg / 8);
        if (checkpoint.NatVerBitmapBytesize != expectedNatBytes || bitmapOffset > payloadBytes
            || checkpoint.NatVerBitmapBytesize > payloadBytes - bitmapOffset)
            throw new InvalidDataException("F2FS NAT version bitmap exceeds the checkpoint payload.");
        _natVersionBitmap = ReadBytes(checked((long)_checkpointBlock * _blockSize + bitmapOffset), checked((int)checkpoint.NatVerBitmapBytesize), "NAT version bitmap");
        var summary = ReadBlockChecked(_checkpointBlock + checkpoint.CpPackStartSum, "Checkpoint NAT journal");
        var journalOffset = (checkpoint.CkptFlags & F2fsCheckpoint.FlagCompactSum) != 0 ? 0 : 512 * 7;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(summary.AsSpan(journalOffset));
        if (count > 38) throw new InvalidDataException("F2FS NAT journal exceeds the summary block.");
        for (var i = 0; i < count; i++)
        {
            var entry = summary.AsSpan(journalOffset + 2 + i * 13, 13);
            var nid = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (!_natJournal.TryAdd(nid, BinaryPrimitives.ReadUInt32LittleEndian(entry[9..])))
                throw new InvalidDataException("Duplicate F2FS NAT journal entry.");
        }
        return checkpoint;
    }

    private F2fsCheckpoint? TryReadCheckpointPack(ulong block)
    {
        try
        {
            var header = ReadBlockChecked(block, "Checkpoint pack header");
            if (!HasValidCheckpointChecksum(header)) return null;
            var checkpoint = F2fsCheckpoint.FromBytes(header);
            if (checkpoint.CpPackTotalBlockCount <= 2 || checkpoint.CpPackTotalBlockCount > _blocksPerSeg) return null;
            var trailer = ReadBlockChecked(block + checkpoint.CpPackTotalBlockCount - 1, "Checkpoint pack trailer");
            return HasValidCheckpointChecksum(trailer) && BinaryPrimitives.ReadUInt64LittleEndian(trailer) == checkpoint.CheckpointVer ? checkpoint : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return null; }
    }

    private static bool HasValidCheckpointChecksum(ReadOnlySpan<byte> block)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(block[164..]);
        if (offset is < 192 or > 4092) return false;
        var crc = ComputeCrc32(F2fsSuperblock.Magic, block[..(int)offset]);
        if (offset < 4092) crc = ComputeCrc32(crc, block[((int)offset + 4)..]);
        return crc == BinaryPrimitives.ReadUInt32LittleEndian(block[(int)offset..]);
    }

    private Dictionary<string, byte[]> ReadXattrs(uint nid, F2fsInode inode)
    {
        using var attributes = new MemoryStream();
        if (inode.InlineXattrAddrs > 0)
        {
            var node = ReadInodeNode(nid);
            attributes.Write(node.AsSpan(F2fsNode.AddrArrayOffset + (F2fsNode.DefAddrsPerInode - inode.InlineXattrAddrs) * 4, inode.InlineXattrAddrs * 4));
        }
        if (inode.XattrNid != 0)
        {
            var node = ReadNodeBlockCached(inode.XattrNid, "xattr node");
            var footer = F2fsNodeFooter.FromBytes(node);
            if (footer.Nid != inode.XattrNid || footer.Ino != nid) throw new InvalidDataException("Invalid F2FS xattr owner.");
            attributes.Write(node.AsSpan(0, F2fsNode.NodeSize - 24));
        }
        return F2fsXattrs.Decode(attributes.GetBuffer().AsSpan(0, (int)attributes.Length));
    }

    private F2fsInode ReadInode(uint nid)
    {
        var node = ReadInodeNode(nid);
        return F2fsInode.FromBytes(node, (_superblock.Feature & 0x40) != 0);
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


        if (!_natJournal.TryGetValue(nid, out var blockAddress))
        {
            var natBlockIndex = nid / F2fsNode.NatEntryPerBlock;
            if (natBlockIndex >= (ulong)_natVersionBitmap.Length * 8)
                throw new InvalidDataException("F2FS nid exceeds the NAT area.");
            var segOff = natBlockIndex / (uint)_blocksPerSeg;
            var inSeg = natBlockIndex % (uint)_blocksPerSeg;
            var secondCopy = (_natVersionBitmap[natBlockIndex / 8] & (0x80 >> (int)(natBlockIndex % 8))) != 0;
            var natBlockAddress = checked(_superblock.NatBlkaddr + segOff * (uint)_blocksPerSeg * 2 + inSeg + (secondCopy ? (uint)_blocksPerSeg : 0));
            if (!_natBlockCache.TryGetValue(natBlockIndex, out var natBlock))
            {
                natBlock = ReadBlockChecked(natBlockAddress, $"NAT block for nid {nid}");
                if (_natBlockCache.Count >= MaxCachedBlocks)
                {
                    _natBlockCache.Clear();
                }
                _natBlockCache[natBlockIndex] = natBlock;
            }
            var entryOffset = checked((int)(nid % F2fsNode.NatEntryPerBlock) * F2fsNode.NatEntrySize);
            blockAddress = BinaryPrimitives.ReadUInt32LittleEndian(natBlock.AsSpan(entryOffset + F2fsNode.NatEntryBlockOffset, 4));
        }
        if (blockAddress < _superblock.MainBlkaddr || blockAddress >= _superblock.BlockCount)
        {
            throw new InvalidDataException($"NAT entry for nid {nid} is missing.");
        }

        return blockAddress;
    }

    /// <summary>
    /// Reads a node block by nid through the bounded node-block cache, avoiding the repeated
    /// full re-read of the same node when sequential reads walk the mapping chain per block.
    /// <para>经有界节点块缓存按 nid 读取节点块，避免顺序读取逐块重走映射链时反复整块重读同一节点。</para>
    /// </summary>
    private byte[] ReadNodeBlockCached(uint nid, string what)
    {
        lock (_streamLock)
        {
            if (_nodeBlockCache.TryGetValue(nid, out var cached))
            {
                return cached;
            }
        }

        var node = ReadBlockChecked(ReadNatBlockAddress(nid), what);
        lock (_streamLock)
        {
            if (_nodeBlockCache.Count >= MaxCachedBlocks)
            {
                _nodeBlockCache.Clear();
            }
            _nodeBlockCache[nid] = node;
        }
        return node;
    }

    internal long? MapBlock(F2fsInode inode, uint logicalBlock)
    {
        if (inode.Extent.Contains(logicalBlock))
        {
            return inode.Extent.Map(logicalBlock);
        }

        if (logicalBlock < inode.AddressCount)
        {
            var direct = inode.Addr[logicalBlock + inode.ExtraIsizeWords];
            return direct == 0 ? null : direct;
        }

        var x = (long)logicalBlock - inode.AddressCount;
        var addressesPerNode = inode.DataAddressesPerNode;
        if (x < addressesPerNode)
        {
            return ReadDnodeAddress(inode.Nid[0], (int)x, logicalBlock);
        }

        x -= addressesPerNode;
        if (x < addressesPerNode)
        {
            return ReadDnodeAddress(inode.Nid[1], (int)x, logicalBlock);
        }

        x -= addressesPerNode;
        var perIndirect = (long)F2fsNode.AddrsPerBlock * addressesPerNode;
        if (x < perIndirect)
        {
            var level1 = (int)(x / addressesPerNode);
            var level2 = (int)(x % addressesPerNode);
            return ReadIndirectAddress(inode.Nid[2], level1, level2, logicalBlock);
        }


        x -= perIndirect;
        if (x < perIndirect)
        {
            return ReadIndirectAddress(inode.Nid[3], (int)(x / addressesPerNode), (int)(x % addressesPerNode), logicalBlock);
        }
        x -= perIndirect;
        var perDoubleIndirect = perIndirect * F2fsNode.AddrsPerBlock;
        if (x < perDoubleIndirect)
        {
            var level1 = (int)(x / perIndirect);
            var level2 = (int)(x % perIndirect);
            return ReadDoubleIndirectAddress(inode.Nid[4], level1, level2, logicalBlock, addressesPerNode);
        }


        return null;
    }

    private long? ReadDnodeAddress(uint nodeNid, int index, uint logicalBlock)
    {
        if (nodeNid == 0 || index < 0 || index >= F2fsNode.AddrsPerBlock)
        {
            return null;
        }

        var node = ReadNodeBlockCached(nodeNid, $"Direct node {nodeNid} (logical {logicalBlock})");
        var address = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(index * 4, 4));
        return address == 0 ? null : address;
    }

    private long? ReadIndirectAddress(uint indirectNid, int level1, int level2, uint logicalBlock)
    {
        if (indirectNid == 0 || level1 < 0 || level1 >= F2fsNode.AddrsPerBlock)
        {
            return null;
        }

        var node = ReadNodeBlockCached(indirectNid, $"Indirect node {indirectNid} (logical {logicalBlock})");
        var directNid = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(level1 * 4, 4));
        if (directNid == 0)
        {
            return null;
        }

        return ReadDnodeAddress(directNid, level2, logicalBlock);
    }

    /// <summary>
    /// Reads a double-indirect node, then a single-indirect node, then a direct node to resolve a data block address.
    /// <para>读 double indirect 节点 → single indirect 节点 → direct 节点 → 数据块。</para>
    /// </summary>
    private long? ReadDoubleIndirectAddress(
        uint doubleIndirectNid,
        int level1,
        int level2,
        uint logicalBlock, int addressesPerNode)
    {
        if (doubleIndirectNid == 0 || level1 < 0 || level1 >= F2fsNode.AddrsPerBlock)
        {
            return null;
        }

        var doubleNode = ReadNodeBlockCached(
            doubleIndirectNid,
            $"Double indirect node {doubleIndirectNid} (logical {logicalBlock})");


        var singleIndirectNid = BinaryPrimitives.ReadUInt32LittleEndian(doubleNode.AsSpan(level1 * 4, 4));
        if (singleIndirectNid == 0)
        {
            return null;
        }


        var siLevel1 = level2 / addressesPerNode;
        var siLevel2 = level2 % addressesPerNode;


        return ReadIndirectAddress(singleIndirectNid, siLevel1, siLevel2, logicalBlock);
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

        if (inode.IsCompressed)
        {
            WriteCompressedFileData(inode, output);
            return;
        }


        var perIndirect = (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock;
        var perDoubleIndirect = perIndirect * F2fsNode.AddrsPerBlock;
        var maxSupportedBlocks = checked(
            inode.AddressCount + 2L * F2fsNode.AddrsPerBlock + 2 * perIndirect + perDoubleIndirect);
        var maxSupportedSize = checked(maxSupportedBlocks * _blockSize);
        if (inode.Size > (ulong)maxSupportedSize)
        {
            throw new InvalidDataException("F2FS file data exceeds the supported block-mapping depth.");
        }

        SharedKernel.Streams.MappedBlockCopy.Copy(_stream, output, checked((long)inode.Size), _blockSize,
            logical => MapBlock(inode, logical));
    }

    /// <summary>
    /// Compressed-file read path: reads cluster by cluster (cluster = 1 &lt;&lt; (log_cluster_size + 12) bytes).
    /// <para>压缩文件读路径：按压缩簇（1 &lt;&lt; (log_cluster_size + 12) 字节）逐簇读取。</para>
    /// </summary>
    private void WriteCompressedFileData(F2fsInode inode, Stream output)
    {


        var logShift = inode.LogClusterSize + 12;
        if (logShift is < 14 or > 20)
        {
            throw new InvalidDataException(
                $"F2FS compressed cluster log size {inode.LogClusterSize} is out of range.");
        }

        var clusterSize = 1 << logShift;
        var clusterPages = clusterSize / _blockSize;
        if (clusterPages <= 0)
        {
            throw new InvalidDataException("F2FS compressed cluster has non-positive page count.");
        }

        var blockSize = _blockSize;
        var remaining = (long)inode.Size;
        var blockCount = (uint)((remaining + blockSize - 1) / blockSize);
        var algorithm = MapCompressAlgorithm(inode.CompressAlgorithm);

        for (var logical = 0u; logical < blockCount; logical += (uint)clusterPages)
        {
            var clusterBlocks = (int)Math.Min(clusterPages, blockCount - logical);
            var writeLength = (int)Math.Min(clusterSize, remaining);
            if (clusterBlocks <= 0 || writeLength <= 0)
            {
                break;
            }

            var physical = MapBlock(inode, logical);
            if (physical == uint.MaxValue - 1)
            {
                var firstAddress = MapBlock(inode, logical + 1);
                if (firstAddress is null or <= 0 or >= uint.MaxValue - 1)
                    throw new InvalidDataException("F2FS compressed cluster has no payload block.");
                var firstBlock = ReadBlockChecked((ulong)firstAddress, "Compressed cluster header");
                var header = F2fsCompressedCluster.FromBytes(firstBlock);
                var compressedBytes = header.CompressedSize;
                if (compressedBytes <= 0 || compressedBytes > (clusterPages - 1) * blockSize - F2fsCompressedCluster.HeaderSize)
                    throw new InvalidDataException("F2FS compressed cluster length exceeds its reserved blocks.");
                var compressed = new byte[compressedBytes];
                var copied = 0;
                var blocksToRead = (F2fsCompressedCluster.HeaderSize + compressedBytes + blockSize - 1) / blockSize;
                for (var b = 0; b < blocksToRead && copied < compressedBytes; b++)
                {
                    byte[] blockData;
                    if (b == 0)
                    {
                        blockData = firstBlock;
                    }
                    else
                    {
                        var blkAddr = MapBlock(inode, logical + 1 + (uint)b);
                        if (blkAddr is null or <= 0 or >= uint.MaxValue - 1)
                            throw new InvalidDataException("F2FS compressed payload block is missing.");

                        blockData = ReadBlockChecked((ulong)blkAddr, $"Compressed cluster block {b} (logical {logical + (uint)b})");
                    }

                    var srcOffset = b == 0 ? F2fsCompressedCluster.HeaderSize : 0;
                    var available = blockData.Length - srcOffset;
                    if (available <= 0)
                    {
                        break;
                    }

                    var toCopy = Math.Min(available, compressedBytes - copied);
                    blockData.AsSpan(srcOffset, toCopy).CopyTo(compressed.AsSpan(copied));
                    copied += toCopy;
                }

                if (copied < compressedBytes)
                {
                    throw new InvalidDataException(
                        $"F2FS compressed cluster at logical block {logical} was truncated ({copied}/{compressedBytes} bytes).");
                }

                if ((inode.CompressFlags & 1) != 0 && ComputeCrc32(F2fsSuperblock.Magic, compressed) != header.Checksum)
                    throw new InvalidDataException("F2FS compressed cluster checksum mismatch.");
                var decompressed = F2fsCompression.DecompressCluster(
                    compressed,
                    algorithm,
                    clusterSize,
                    clusterSize);

                if (decompressed.Length != clusterSize)
                    throw new InvalidDataException("F2FS compressed cluster has an incorrect decompressed length.");
                output.Write(decompressed, 0, writeLength);

                remaining -= writeLength;
            }
            else
            {


                var clusterBuffer = new byte[clusterSize];
                for (var b = 0; b < clusterBlocks; b++)
                {
                    var blkAddr = MapBlock(inode, logical + (uint)b);
                    if (blkAddr is null or 0 or uint.MaxValue)
                    {
                        continue;
                    }

                    var blockData = ReadBlockChecked((ulong)blkAddr, $"Uncompressed cluster block {b} (logical {logical + (uint)b})");
                    blockData.AsSpan(0, Math.Min(blockSize, clusterBuffer.Length - b * blockSize))
                        .CopyTo(clusterBuffer.AsSpan(b * blockSize));
                }

                output.Write(clusterBuffer, 0, writeLength);
                remaining -= writeLength;
            }
        }
    }

    /// <summary>
    /// kernel: 0=LZO,1=LZ4,2=ZSTD,3=LZORLE。
    /// <para>将 inode extra attributes 内的内核算法 ID 映射为 CompressionAlgorithm。</para>
    /// </summary>
    private static CompressionAlgorithm MapCompressAlgorithm(byte kernelAlgorithmId)
        => kernelAlgorithmId switch
        {
            1 => CompressionAlgorithm.Lz4,
            2 => CompressionAlgorithm.Zstd,


            0 or 3 => CompressionAlgorithm.Lzo,
            _ => throw new InvalidDataException(
                $"Unknown F2FS compression algorithm id {kernelAlgorithmId}."),
        };

    /// <summary>
    /// Shared zero buffer for hole/zero writes, avoiding a fresh allocation per call.
    /// <para>空洞/零写入的共享零缓冲，避免每次调用新分配。</para>
    /// </summary>
    private static readonly byte[] ZeroBuffer = new byte[1024 * 1024];

    private static void WriteZeros(Stream output, int count)
    {
        if (count <= 0)
        {
            return;
        }

        while (count > 0)
        {
            var n = Math.Min(count, ZeroBuffer.Length);
            output.Write(ZeroBuffer, 0, n);
            count -= n;
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
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
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