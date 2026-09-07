using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Core;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed class Ext4Parser : IDisposable
{
    public enum Ext4EntryKind
    {
        File,

        Directory,

        Symlink,
    }

    public sealed record Ext4FileEntry(string Path, long Size, uint Inode, Ext4EntryKind Kind, string? SymlinkTarget = null, IReadOnlyDictionary<string, byte[]>? Xattrs = null, uint Uid = 0, uint Gid = 0, ushort Mode = 0);

    private const int SuperblockReadLength = 1024 + 1024;
    private const uint RootInodeNumber = 2;
    private const uint Ext4XattrMagic = 0xEA020000;
    private const int MaxExtentLeafPointers = 12;
    private const long MaximumGroupDescriptorTableSize = 512L * 1024 * 1024;
    private const int MaxDirectoryDepth = 256;

    private const uint FeatureIncompatEaInode = 0x0400;

    private const uint FeatureRoCompatMetadataCsum = 0x0400;

    private const int MaxXattrValueSize = 64 * 1024;

    private static readonly string[] XattrNameIndexPrefixes =
    [
        "",
        "user.",
        "system.posix_acl_access",
        "system.posix_acl_default",
        "trusted.",
        "",
        "security.",
        "system.",
        "system.richacl",
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

    public long PartitionSize => checked((long)_superblock.BlocksCount * _blockSize);

    public long BlockSize => _blockSize;

    public ulong BlockCount => _superblock.BlocksCount;

    public uint InodeCount => _superblock.InodesCount;

    public int FileCount => _entries.Values.Count(e => e.Kind != Ext4EntryKind.Directory);

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


        const uint supportedIncompat = 0x0001 | 0x0002 | 0x0004
            | 0x0040 | 0x0080 | 0x0200 | 0x0400
            | 0x2000 | 0x4000 | 0x8000 | 0x10000;

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
    /// Walks the directory tree from the root inode (2) and returns all entries (files, directories and symlinks) with their relative paths.
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
    /// Writes the full content of the file at path to output, zero-filling sparse holes.
    /// <para>将 path 处文件的完整内容写入 output， 稀疏洞补零；符号链接输出其目标字节。</para>
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
            var targetPath = SharedKernel.FileExtraction.ResolvePath(outputDirectory, entry.Path);
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
                        SharedKernel.FileExtraction.WriteAtomic(targetPath, output => output.Write(Encoding.UTF8.GetBytes(entry.SymlinkTarget ?? string.Empty)), overwrite);
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

            SharedKernel.FileExtraction.WriteAtomic(targetPath, output => ReadFile(entry.Path, output), overwrite);

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


        if (depth > MaxDirectoryDepth)
        {
            throw new InvalidDataException($"ext4 directory tree exceeds the maximum depth of {MaxDirectoryDepth}.");
        }

        if (!visitedDirectories.Add(dirInode))
        {


            throw new InvalidDataException($"ext4 directory cycle detected at inode {dirInode}.");
        }

        var inode = ReadInode(dirInode);
        var blockCount = inode.HasInlineData ? 2u : (uint)((inode.Size + _blockSize - 1) / _blockSize);
        for (var logical = 0u; logical < blockCount; logical++)
        {
            byte[] block;
            if (inode.HasInlineData)
            {
                // Inline directories have a parent inode followed by two independent dirent regions.
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

                _entries[childPath] = new Ext4FileEntry(childPath, child.Size, entry.Inode, kind, symlinkTarget, childXattrs, (uint)child.UidLo, (uint)child.GidLo, child.Mode);
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
    /// <para>从 inode 的额外空间解析内联扩展属性（xattr）。</para>
    /// </summary>
    private static Dictionary<string, byte[]>? ParseInlineXattrs(byte[] inodeRaw, int inodeSize)
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
        if (magic != Ext4XattrMagic)
        {
            return null;
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);


        var pos = xattrStart + 4;
        while (pos + 16 <= inodeSize)
        {
            var nameLen = inodeRaw[pos];
            if (BinaryPrimitives.ReadUInt32LittleEndian(inodeRaw.AsSpan(pos)) == 0)
            {
                break;
            }

            var valueOffs = BinaryPrimitives.ReadUInt16LittleEndian(inodeRaw.AsSpan(pos + 2, 2));
            var valueInode = BinaryPrimitives.ReadUInt32LittleEndian(inodeRaw.AsSpan(pos + 4, 4));
            var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeRaw.AsSpan(pos + 8, 4));
            var nameStart = pos + 16;
            var nameEnd = nameStart + nameLen;
            if (nameEnd > inodeSize)
            {
                break;
            }

            var name = ResolveXattrName(inodeRaw[pos + 1], Encoding.UTF8.GetString(inodeRaw, nameStart, nameLen));
            var absValueOffset = xattrStart + 4 + valueOffs;
            if (valueInode == 0 && absValueOffset <= inodeSize && valueSize <= inodeSize - absValueOffset)
            {
                result[name] = inodeRaw.AsSpan(absValueOffset, (int)valueSize).ToArray();
            }


            pos += (16 + nameLen + 3) & ~3;
        }

        return result.Count > 0 ? result : null;
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
                merged[kv.Key] = kv.Value;
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
        if (magic != Ext4XattrMagic)
        {
            return null;
        }


        var hasCsum = (_superblock.FeatureRoCompat & FeatureRoCompatMetadataCsum) != 0;
        var valueRegionEnd = hasCsum ? block.Length - 4 : block.Length;


        var eaInodeEnabled = (_superblock.FeatureIncompat & FeatureIncompatEaInode) != 0;
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pos = 4;
        while (pos + 12 <= block.Length)
        {
            var nameLen = block[pos];
            if (nameLen == 0)
            {
                break;
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


            byte[]? value = null;
            if (eaInodeEnabled && valueSize > (uint)block.Length)
            {


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


            pos += (12 + nameLen + 3) & ~3;
        }

        return result.Count > 0 ? result : null;
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

        return suffix;
    }

    /// <summary>
    /// Reads the value data from a dedicated ea_inode when a large xattr value is stored in its own inode.
    /// <para>当大型 xattr 值存于其自身 inode 时，从专用 ea_inode 读取值数据。</para>
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


            return System.Text.Encoding.UTF8.GetString(inode.BlockData, 0, (int)inode.Size);
        }


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
