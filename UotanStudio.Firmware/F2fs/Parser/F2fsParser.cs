using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

public sealed record F2fsFileEntry(string Path, long Size, uint Inode, F2fsParser.F2fsEntryKind Kind, string? SymlinkTarget = null);

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

    public long ImageSize => _imageSize;

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
        ValidateCheckpointChecksum();
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
            System.Diagnostics.Debug.WriteLine($"F2FS superblock CRC mismatch: stored=0x{storedCrc:X8}, computed=0x{computed:X8}.");
        }
    }

    private void ValidateCheckpointChecksum()
    {
        var cpOffset = _superblock.CpBlkaddr * _blockSize;
        var raw = ReadBytes(cpOffset, _blockSize, "Checkpoint CRC");
        var checksumOffset = _checkpoint.ChecksumOffset;
        if (checksumOffset == 0 || checksumOffset + 4 > _blockSize)
        {
            return;
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)checksumOffset, 4));
        if (storedCrc == 0)
        {
            return;
        }

        var computed = ComputeCrc32(F2fsSuperblock.Magic, raw.AsSpan(0, (int)checksumOffset));
        if (computed != storedCrc)
        {
            System.Diagnostics.Debug.WriteLine($"F2FS checkpoint CRC mismatch: stored=0x{storedCrc:X8}, computed=0x{computed:X8}.");
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var entries = ListFiles();
        var totalCount = entries.Count;
        var processedCount = 0;
        foreach (var entry in entries)
        {
            var targetPath = Path.Combine(outputDirectory, entry.Path);
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

            using (var output = File.Create(targetPath))
            {
                ReadFile(entry.Path, output);
            }

            processedCount++;
            progress?.Report((processedCount, totalCount));
        }
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
        var pack1 = ReadBlockChecked(_superblock.CpBlkaddr, "Checkpoint");
        var checkpoint = F2fsCheckpoint.FromBytes(pack1);
        if (checkpoint.CheckpointVer != 0)
        {
            return checkpoint;
        }

        var pack2Block = checked(_superblock.CpBlkaddr + _blocksPerSeg);
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


        var natBlockIndex = nid / F2fsNode.NatEntryPerBlock;
        var segOff = natBlockIndex / 512;
        var inSeg = natBlockIndex % 512;
        var natBlockAddress = checked(_superblock.NatBlkaddr + segOff * 1024u + inSeg);
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
        var perDoubleIndirect = perIndirect * F2fsNode.AddrsPerBlock;
        if (x < perDoubleIndirect)
        {
            var level1 = (int)(x / perIndirect);
            var level2 = (int)(x % perIndirect);
            return ReadDoubleIndirectAddress(inode.Nid[3], level1, level2, logicalBlock);
        }


        x -= perDoubleIndirect;
        if (x < perDoubleIndirect)
        {
            var level1 = (int)(x / perIndirect);
            var level2 = (int)(x % perIndirect);
            return ReadDoubleIndirectAddress(inode.Nid[4], level1, level2, logicalBlock);
        }


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

    /// <summary>
    /// Reads a double-indirect node, then a single-indirect node, then a direct node to resolve a data block address.
    /// <para>读 double indirect 节点 → single indirect 节点 → direct 节点 → 数据块。</para>
    /// </summary>
    private long? ReadDoubleIndirectAddress(
        uint doubleIndirectNid,
        int level1,
        int level2,
        uint logicalBlock)
    {
        if (doubleIndirectNid == 0 || level1 < 0 || level1 >= F2fsNode.AddrsPerBlock)
        {
            return null;
        }

        var doubleNode = ReadBlockChecked(
            ReadNatBlockAddress(doubleIndirectNid),
            $"Double indirect node {doubleIndirectNid} (logical {logicalBlock})");


        var singleIndirectNid = BinaryPrimitives.ReadUInt32LittleEndian(doubleNode.AsSpan(level1 * 4, 4));
        if (singleIndirectNid == 0)
        {
            return null;
        }


        var siLevel1 = level2 / F2fsNode.AddrsPerBlock;
        var siLevel2 = level2 % F2fsNode.AddrsPerBlock;


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
            F2fsNode.Indirect1Block + perIndirect + 2 * perDoubleIndirect);
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

    /// <summary>
    /// Compressed-file read path: reads cluster by cluster (cluster = 1 &lt;&lt; (log_cluster_size + 12) bytes).
    /// <para>压缩文件读路径：按压缩簇（1 &lt;&lt; (log_cluster_size + 12) 字节）逐簇读取。</para>
    /// </summary>
    private void WriteCompressedFileData(F2fsInode inode, Stream output)
    {


        var logShift = inode.LogClusterSize + 12;
        if (logShift is < 12 or > 16)
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
            if (physical is null || physical == 0)
            {

                WriteZeros(output, writeLength);
                remaining -= writeLength;
                continue;
            }


            var firstBlock = ReadBlockChecked((ulong)physical, $"Compressed cluster first block (logical {logical})");
            var header = F2fsCompressedCluster.FromBytes(firstBlock);

            if (header.IsCompressed && header.UncompressedSize == clusterSize)
            {


                var compressedBytes = header.CompressedSize;
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
                        var blkAddr = MapBlock(inode, logical + (uint)b);
                        if (blkAddr is null || blkAddr == 0)
                        {
                            break;
                        }

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

                var decompressed = F2fsCompression.DecompressCluster(
                    compressed,
                    algorithm,
                    header.UncompressedSize,
                    clusterSize);

                var toWrite = Math.Min(decompressed.Length, writeLength);
                output.Write(decompressed, 0, toWrite);
                if (toWrite < writeLength)
                {
                    WriteZeros(output, writeLength - toWrite);
                }

                remaining -= writeLength;
            }
            else
            {


                var clusterBuffer = new byte[clusterSize];
                for (var b = 0; b < clusterBlocks; b++)
                {
                    var blkAddr = MapBlock(inode, logical + (uint)b);
                    if (blkAddr is null || blkAddr == 0)
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
    /// <para>将 inode i_advise 内的内核算法 ID 映射为 CompressionAlgorithm。</para>
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

    private static void WriteZeros(Stream output, int count)
    {
        if (count <= 0)
        {
            return;
        }

        output.Write(new byte[count], 0, count);
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
