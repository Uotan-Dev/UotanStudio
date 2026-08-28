using System.Buffers.Binary;
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
    public sealed record Ext4FileEntry(string Path, long Size, uint Inode, Ext4EntryKind Kind, string? SymlinkTarget = null);

    private const int SuperblockReadLength = 1024 + 360;
    private const uint RootInodeNumber = 2;
    private const int MaxExtentLeafPointers = 12; // 12 direct pointers in the indirect scheme

    private readonly Stream _stream;
    private Ext4Superblock _superblock = null!;
    private Ext4BlockGroup[] _groups = [];
    private int _blockSize;
    private int _inodeSize;
    private long _inodeTableOffset;
    private readonly Dictionary<string, Ext4FileEntry> _entries = new(StringComparer.Ordinal);
    private bool _walked;

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

        _inodeSize = _superblock.InodeSize == 0 ? 256 : _superblock.InodeSize;
        var descSize = _superblock.EffectiveDescSize;
        var groupCount = checked((int)((_superblock.BlocksCount + _superblock.BlocksPerGroup - 1) / _superblock.BlocksPerGroup));
        if (groupCount <= 0)
        {
            throw new InvalidDataException("ext4 image has no block groups.");
        }

        _groups = new Ext4BlockGroup[groupCount];
        var gdtBytes = new byte[groupCount * descSize];
        _stream.Position = 2048;
        _stream.ReadExactly(gdtBytes);
        for (var i = 0; i < groupCount; i++)
        {
            _groups[i] = Ext4BlockGroup.FromBytes(gdtBytes.AsSpan(i * descSize, descSize), descSize);
        }

        _inodeTableOffset = checked((long)_groups[0].InodeTable * _blockSize);
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
            WalkDirectory(RootInodeNumber, string.Empty);
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

    private void WalkDirectory(uint dirInode, string prefix)
    {
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
                var childPath = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                var kind = ClassifyKind(entry.FileType, child);
                var symlinkTarget = kind == Ext4EntryKind.Symlink ? ReadSymlinkTarget(child) : null;

                _entries[childPath] = new Ext4FileEntry(childPath, child.Size, entry.Inode, kind, symlinkTarget);
                if (kind == Ext4EntryKind.Directory)
                {
                    WalkDirectory(entry.Inode, childPath);
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
        var offset = checked(_inodeTableOffset + ((long)inodeNumber - 1) * _inodeSize);
        var buffer = new byte[_inodeSize];
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
        return Ext4Inode.FromBytes(buffer, _inodeSize);
    }

    private byte[] ReadBlock(ulong physicalBlock)
    {
        var buffer = new byte[_blockSize];
        ReadBlockTo(physicalBlock, buffer);
        return buffer;
    }

    private void ReadBlockTo(ulong physicalBlock, byte[] buffer)
    {
        var offset = checked((long)physicalBlock * _blockSize);
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
}