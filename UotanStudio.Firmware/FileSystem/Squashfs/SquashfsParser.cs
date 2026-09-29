using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.Squashfs;

namespace UotanStudio.Firmware.FileSystem.Squashfs;

/// <summary>
/// Read-only parser for SquashFS version 4 images: superblock validation, id and fragment tables, inode
/// and directory decoding, block-level extraction for the wired compressors (gzip, lzo, xz, lz4, zstd).
/// <para>SquashFS 版本 4 镜像的只读解析器：超级块校验、id 与 fragment 表、inode 与目录解码，以及已接线
/// 压缩器（gzip、lzo、xz、lz4、zstd）的块级解包。</para>
/// </summary>
internal sealed class SquashfsParser
{
    internal const int MaximumDepth = 64;
    internal const int MaximumEntryCount = 1 << 20;
    internal const uint NonCompressionBit = 1u << 24;
    private const int FragmentEntryBytes = 16;
    private const int MaximumBlockCount = 1 << 22;

    private readonly Stream _input;
    private readonly SquashfsSuperblock _superblock;
    private readonly uint[] _ids;
    private readonly (ulong Start, uint Size, uint Uncached)[] _fragments;
    private readonly SquashfsMetadataStream _inodeTable;
    private readonly SquashfsMetadataStream _directoryTable;
    private readonly CancellationToken _cancellationToken;
    private int _entryBudget = MaximumEntryCount;

    /// <summary>Parses the superblock and the referenced tables. <para>解析超级块与其引用的表。</para></summary>
    /// <param name="input">Seekable image stream; not closed by this class. <para>可定位的镜像流；本类不将其关闭。</para></param>
    /// <param name="cancellationToken">Token checked throughout the read. <para>全程检查的令牌。</para></param>
    /// <exception cref="InvalidDataException">Any structural or bounds validation failure.
    /// <para>任何结构或边界校验失败时抛出。</para></exception>
    internal SquashfsParser(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
        {
            throw new InvalidDataException("SquashFS parsing requires a seekable stream.");
        }

        _input = input;
        _cancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> super = stackalloc byte[SquashfsSuperblock.Size];
        input.Position = 0;
        input.ReadExactly(super);
        _superblock = SquashfsSuperblock.Parse(super, input.Length);

        _ids = ReadIdTable(cancellationToken);
        _fragments = ReadFragmentTable(cancellationToken);
        ReadXattrHeader(cancellationToken);
        _inodeTable = new SquashfsMetadataStream(input, (long)_superblock.InodeTableStart, (long)_superblock.BytesUsed, _superblock.BigEndian, _superblock.Compression, cancellationToken);
        _directoryTable = new SquashfsMetadataStream(input, (long)_superblock.DirectoryTableStart, (long)_superblock.BytesUsed, _superblock.BigEndian, _superblock.Compression, cancellationToken);
        Root = ReadInode((long)(_superblock.RootInodeAddress >> 16), (int)(_superblock.RootInodeAddress & 0xFFFF));
        if (Root.Kind != SquashfsEntryKind.Directory)
        {
            throw new InvalidDataException("The SquashFS root inode is not a directory.");
        }
    }

    internal SquashfsSuperblock Superblock => _superblock;

    /// <summary>Decoded uid/gid table values in table order. <para>按表顺序解码出的 uid/gid 值。</para></summary>
    internal IReadOnlyList<uint> Ids => _ids;

    /// <summary>Parsed root directory inode. <para>已解析的根目录 inode。</para></summary>
    internal SquashfsInode Root { get; }

    /// <summary>Xattr id count when an xattr table exists; -1 for images without xattrs.
    /// <para>存在 xattr 表时的 xattr id 计数；无 xattr 镜像为 -1。</para></summary>
    internal long XattrIdCount { get; private set; } = -1;

    /// <summary>Walks the tree and yields every entry with its slash-separated image path.
    /// <para>遍历目录树并产出全部条目及其斜杠分隔镜像路径。</para></summary>
    /// <exception cref="InvalidDataException">Caps are exceeded or the structure is corrupt.
    /// <para>超出上限或结构损坏时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The stored token was signaled.
    /// <para>保存的令牌被触发。</para></exception>
    internal IReadOnlyList<SquashfsEntry> EnumerateEntries()
    {
        var results = new List<SquashfsEntry>();
        Walk(Root, "", 0, results, Token);
        return results;
    }

    /// <summary>
    /// Extracts the tree into a directory. Symbolic links become regular files carrying their target text;
    /// device, FIFO and socket entries are listed but not written.
    /// <para>将目录树解包到本地目录。符号链接成为承载目标文本的普通文件；设备、FIFO 与套接字条目仅列出
    /// 而不写出。</para>
    /// </summary>
    /// <param name="outputDirectory">Destination root. <para>目标根目录。</para></param>
    /// <param name="overwrite">Replace existing destination files when <see langword="true"/>.
    /// <para>为 <see langword="true"/> 时覆盖已存在文件。</para></param>
    /// <param name="maximumOutputBytes">Cumulative write budget for the whole extraction.
    /// <para>整个解包过程的累计写入预算。</para></param>
    /// <param name="byteProgress">Cumulative processed/total reporter; may be <see langword="null"/>.
    /// <para>累计已处理/总字节回报器；可为 <see langword="null"/>。</para></param>
    /// <returns>The image paths written. <para>已写出的镜像路径。</para></returns>
    /// <exception cref="InvalidDataException">An entry escapes the output directory or exceeds a bound.
    /// <para>条目逃逸输出目录或越界时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The stored token was signaled.
    /// <para>保存的令牌被触发。</para></exception>
    internal IReadOnlyList<string> ExtractToDirectory(
        string outputDirectory,
        bool overwrite,
        long maximumOutputBytes,
        IProgress<(long Processed, long Total)>? byteProgress)
    {
        var entries = EnumerateEntries();
        var total = 0L;
        foreach (var entry in entries)
        {
            if (entry.Kind is SquashfsEntryKind.RegularFile or SquashfsEntryKind.SymbolicLink)
            {
                total = checked(total + Math.Max(entry.Size, 0));
            }
        }

        var written = new List<string>();
        var processed = 0L;
        byteProgress?.Report((0, total));
        foreach (var entry in entries)
        {
            Token.ThrowIfCancellationRequested();
            switch (entry.Kind)
            {
                case SquashfsEntryKind.Directory:
                    Directory.CreateDirectory(FileExtraction.ResolvePath(outputDirectory, entry.Path));
                    break;
                case SquashfsEntryKind.RegularFile:
                case SquashfsEntryKind.SymbolicLink:
                    WriteFile(entry, outputDirectory, overwrite, ref processed, maximumOutputBytes, total, byteProgress);
                    written.Add(entry.Path);
                    break;
            }
        }

        byteProgress?.Report((processed, total));
        return written;
    }

    private CancellationToken Token => _cancellationToken;

    private void WriteFile(
        SquashfsEntry entry,
        string outputDirectory,
        bool overwrite,
        ref long processed,
        long maximumOutputBytes,
        long total,
        IProgress<(long Processed, long Total)>? byteProgress)
    {
        var target = FileExtraction.ResolvePath(outputDirectory, entry.Path);
        var size = entry.Kind == SquashfsEntryKind.SymbolicLink
            ? entry.Inode.Target?.LongLength ?? 0
            : entry.Size;
        if (size < 0)
        {
            size = 0;
        }

        var remaining = Math.Max(0, maximumOutputBytes - processed);
        if (size > remaining)
        {
            throw new InvalidDataException(
                $"SquashFS extraction of '{entry.Path}' would exceed the {maximumOutputBytes}-byte output budget.");
        }

        if (entry.Kind == SquashfsEntryKind.SymbolicLink)
        {
            var bytes = entry.Inode.Target ?? [];
            FileExtraction.WriteAtomic(target, stream => stream.Write(bytes), overwrite, Token);
            processed = checked(processed + bytes.LongLength);
            byteProgress?.Report((processed, total));
            return;
        }

        FileExtraction.WriteAtomic(target, stream => WriteInodeData(entry.Inode, stream, Token), overwrite, Token);
        processed = checked(processed + size);
        byteProgress?.Report((processed, total));
    }

    /// <summary>Streams the data bytes of one file inode into the destination.
    /// <para>将单个文件 inode 的数据字节流传写入目标。</para></summary>
    /// <param name="inode">Regular file inode. <para>常规文件 inode。</para></param>
    /// <param name="destination">Writable destination. <para>可写目标。</para></param>
    /// <param name="cancellationToken">Token checked per block. <para>逐块检查的令牌。</para></param>
    internal void WriteInodeData(SquashfsInode inode, Stream destination, CancellationToken cancellationToken)
    {
        var blockSize = _superblock.BlockSize;
        var address = (long)inode.DataStart;
        var position = 0L;
        for (var i = 0; i < inode.BlockSizes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = Math.Min((long)blockSize, inode.Size - position);
            var word = inode.BlockSizes[i];
            if (word == 0)
            {
                ZeroFill(destination, expected, cancellationToken);
                position += expected;
                continue;
            }

            var stored = (word & NonCompressionBit) != 0;
            var length = word & (NonCompressionBit - 1);
            if (length == 0 || length > (uint)blockSize || word >> 25 != 0)
            {
                throw new InvalidDataException($"SquashFS block word 0x{word:X8} declares an impossible length.");
            }

            FileExtraction.ValidateRange(address, length, (long)_superblock.BytesUsed, "SquashFS data block");
            var payload = new byte[length];
            _input.Position = address;
            _input.ReadExactly(payload);
            address = checked(address + length);

            byte[] decoded;
            if (stored)
            {
                if (length != expected)
                {
                    throw new InvalidDataException(
                        $"A stored SquashFS data block holds {length} bytes but {expected} were expected.");
                }

                decoded = payload;
            }
            else
            {
                decoded = SquashfsDecompression.Decompress(_superblock.Compression, payload, blockSize, exactOutput: false);
                if (decoded.LongLength != blockSize && decoded.LongLength != expected)
                {
                    throw new InvalidDataException(
                        $"A SquashFS data block decoded to {decoded.LongLength} bytes; expected {blockSize} or {expected}.");
                }
            }

            var take = (int)Math.Min(decoded.LongLength, expected);
            destination.Write(decoded.AsSpan(0, take));
            position += take;
        }

        if (inode.FragmentIndex >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            position = WriteFragmentTail(inode, destination, position, cancellationToken);
        }

        if (position != inode.Size)
        {
            throw new InvalidDataException(
                $"The SquashFS inode {inode.InodeNumber} produced {position} bytes but declares {inode.Size}.");
        }
    }

    /// <summary>Appends the fragment-backed tail of one inode and returns the advanced byte position.
    /// <para>追加单个 inode 由 fragment 承载的尾部字节，并返回推进后的字节位置。</para></summary>
    private long WriteFragmentTail(SquashfsInode inode, Stream destination, long position, CancellationToken cancellationToken)
    {
        var tail = inode.Size - position;
        if (tail <= 0)
        {
            return position;
        }

        if (inode.FragmentIndex >= _fragments.Length)
        {
            throw new InvalidDataException(
                $"The SquashFS inode {inode.InodeNumber} references fragment {inode.FragmentIndex} beyond the {_fragments.Length}-entry table.");
        }

        var fragment = _fragments[inode.FragmentIndex];
        if (fragment.Uncached != 0)
        {
            throw new InvalidDataException(
                $"SquashFS uncached fragments (fragment {inode.FragmentIndex}) are not supported by this reader.");
        }

        var stored = (fragment.Size & NonCompressionBit) != 0;
        var length = fragment.Size & (NonCompressionBit - 1);
        if (length == 0 || length > (uint)_superblock.BlockSize || fragment.Size >> 25 != 0)
        {
            throw new InvalidDataException($"SquashFS fragment {inode.FragmentIndex} declares an impossible size.");
        }

        FileExtraction.ValidateRange((long)fragment.Start, length, (long)_superblock.BytesUsed, "SquashFS fragment block");
        var payload = new byte[length];
        _input.Position = (long)fragment.Start;
        _input.ReadExactly(payload);
        var decoded = stored
            ? payload
            : SquashfsDecompression.Decompress(_superblock.Compression, payload, _superblock.BlockSize, exactOutput: false);
        var from = inode.FragmentOffset;
        if (from < 0 || from + tail > decoded.LongLength)
        {
            throw new InvalidDataException(
                $"The SquashFS fragment slice {from}..{from + tail} exceeds the decoded {decoded.LongLength}-byte block.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        destination.Write(decoded.AsSpan(from, (int)tail));
        return checked(position + tail);
    }

    private static void ZeroFill(Stream destination, long count, CancellationToken cancellationToken)
    {
        var chunk = new byte[Math.Min(count, 81920)];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = (int)Math.Min(chunk.Length, count);
            destination.Write(chunk, 0, take);
            count -= take;
        }
    }

    private void Walk(SquashfsInode directory, string parentPath, int depth, List<SquashfsEntry> sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDepth)
        {
            throw new InvalidDataException("The SquashFS directory tree exceeds the supported depth.");
        }

        foreach (var (name, inode) in ReadDirectoryEntries(directory))
        {
            if (--_entryBudget < 0)
            {
                throw new InvalidDataException("The SquashFS image stores more entries than the supported limit.");
            }

            var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
            sink.Add(new SquashfsEntry { Path = path, Inode = inode });
            if (inode.Kind == SquashfsEntryKind.Directory)
            {
                Walk(inode, path, depth + 1, sink, cancellationToken);
            }
        }
    }

    private IEnumerable<(string Name, SquashfsInode Inode)> ReadDirectoryEntries(SquashfsInode directory)
    {
        var cursor = _directoryTable.CreateCursor(directory.DirectoryBlock, directory.DirectoryOffset);
        var consumed = 0L;
        while (consumed < directory.DirectorySize)
        {
            var header = cursor.ReadBytes(12);
            consumed += 12;
            var count = U32(header, 0);
            var blockStart = (long)U32(header, 4);
            if (count > 255)
            {
                throw new InvalidDataException($"The SquashFS directory header declares {count + 1} entries; the format allows at most 256.");
            }

            for (var i = 0; i <= (int)count; i++)
            {
                var fixedPart = cursor.ReadBytes(8);
                consumed += 8;
                var offset = U16(fixedPart, 0);
                var type = U16(fixedPart, 4);
                var nameLength = U16(fixedPart, 6) + 1u;
                if (nameLength > 256)
                {
                    throw new InvalidDataException($"The SquashFS directory entry name is {nameLength} bytes; the format allows at most 256.");
                }

                var nameBytes = cursor.ReadBytes((int)nameLength);
                consumed += nameLength;
                var name = DecodeName(nameBytes);
                var inode = ReadInode(blockStart, offset);
                ValidateEntryType(type);
                yield return (name, inode);
            }
        }
    }

    private static void ValidateEntryType(ushort dirType)
    {
        if (dirType is 0 or > 14)
        {
            throw new InvalidDataException($"The SquashFS directory entry type {dirType} is unknown.");
        }
    }

    private SquashfsInode ReadInode(long relativeBlock, int offset)
    {
        var cursor = _inodeTable.CreateCursor(relativeBlock, offset);
        var baseFields = cursor.ReadBytes(16);
        var rawType = U16(baseFields, 0);
        var mode = U16(baseFields, 2);
        var uid = U16(baseFields, 4);
        var gidIndex = U16(baseFields, 6);
        var mtime = U32(baseFields, 8);
        var inodeNumber = U32(baseFields, 12);
        if (uid >= _superblock.IdCount || gidIndex >= _superblock.IdCount)
        {
            throw new InvalidDataException($"The SquashFS inode {inodeNumber} references id slot {Math.Max(uid, gidIndex)} beyond the table of length {_superblock.IdCount}.");
        }

        var kind = MapKind(rawType, out var extended);
        var inode = new SquashfsInode
        {
            Kind = kind,
            RawType = rawType,
            Mode = mode,
            UidIndex = uid,
            GidIndex = gidIndex,
            ModifiedTime = mtime,
            InodeNumber = inodeNumber,
        };
        return kind switch
        {
            SquashfsEntryKind.Directory => ReadDirectoryInode(cursor, inode, extended),
            SquashfsEntryKind.RegularFile => ReadFileInode(cursor, inode, extended),
            SquashfsEntryKind.SymbolicLink => ReadSymlinkInode(cursor, inode, extended),
            _ => ReadSpecialInode(cursor, inode, kind, extended)
        };
    }

    /// <summary>
    /// Completes a directory inode from its compact or extended body layout.
    /// <para>按紧凑或扩展的目录 inode 布局补全基础 inode。</para>
    /// </summary>
    private SquashfsInode ReadDirectoryInode(SquashfsMetadataStream.Cursor cursor, SquashfsInode inode, bool extended)
    {
        var body = cursor.ReadBytes(extended ? 24 : 16);
        if (extended)
        {
            inode.DirectorySize = U32(body, 4);
            inode.DirectoryBlock = U32(body, 8);
            inode.ParentInode = U32(body, 12);
            inode.DirectoryOffset = U16(body, 18);
            inode.XattrIndex = InvalidIfSentinel(U32(body, 20));
        }
        else
        {
            inode.DirectoryBlock = U32(body, 0);
            inode.DirectorySize = U16(body, 8) + 3L;
            inode.DirectoryOffset = U16(body, 10);
            inode.ParentInode = U32(body, 12);
        }
        return inode;
    }

    /// <summary>
    /// Completes a regular-file inode with its block list and fragment layout, rejecting impossible sizes.
    /// <para>以块列表与 fragment 布局补全常规文件 inode，并拒绝不可能的大小声明。</para>
    /// </summary>
    private SquashfsInode ReadFileInode(SquashfsMetadataStream.Cursor cursor, SquashfsInode inode, bool extended)
    {
        long start;
        long size;
        uint fragment;
        int fragmentOffset;
        bool sparse = false;
        long xattr = -1;
        if (!extended)
        {
            var body = cursor.ReadBytes(16);
            start = U32(body, 0);
            fragment = U32(body, 4);
            fragmentOffset = (int)U32(body, 8);
            size = U32(body, 12);
        }
        else
        {
            var body = cursor.ReadBytes(40);
            start = (long)U64(body, 0);
            size = unchecked((long)U64(body, 8));
            sparse = U64(body, 16) != 0;
            fragment = U32(body, 28);
            fragmentOffset = (int)U32(body, 32);
            xattr = InvalidIfSentinel(U32(body, 36));
        }

        if (size < 0 || size > 1L << 52)
        {
            throw new InvalidDataException($"The SquashFS file inode {inode.InodeNumber} declares the impossible size {size}.");
        }

        var blockCount = ComputeBlockCount(size, fragment);
        var blocks = new uint[blockCount];
        var blockBytes = cursor.ReadBytes(blockCount * 4);
        for (var i = 0; i < blockCount; i++)
        {
            blocks[i] = U32(blockBytes, i * 4);
        }

        inode.XattrIndex = xattr;
        inode.DataStart = (ulong)start;
        inode.Size = size;
        inode.BlockSizes = blocks;
        inode.FragmentIndex = InvalidIfSentinel(fragment);
        inode.FragmentOffset = fragmentOffset;
        inode.Sparse = sparse;
        return inode;
    }

    /// <summary>
    /// Completes a symbolic-link inode with its bounded target bytes.
    /// <para>以有界的目标字节补全符号链接 inode。</para>
    /// </summary>
    private SquashfsInode ReadSymlinkInode(SquashfsMetadataStream.Cursor cursor, SquashfsInode inode, bool extended)
    {
        var body = cursor.ReadBytes(8);
        var targetLength = U32(body, 4);
        if (targetLength > 4096)
        {
            throw new InvalidDataException($"The SquashFS symlink inode {inode.InodeNumber} declares an oversized target.");
        }

        inode.Target = cursor.ReadBytes((int)targetLength);
        inode.Size = inode.Target.LongLength;
        if (extended)
        {
            inode.XattrIndex = InvalidIfSentinel(U32(cursor.ReadBytes(4), 0));
        }
        return inode;
    }

    /// <summary>
    /// Completes FIFO, socket and device inodes; only device nodes carry a device number.
    /// <para>补全 FIFO、socket 与设备 inode；仅设备节点携带设备号。</para>
    /// </summary>
    private SquashfsInode ReadSpecialInode(SquashfsMetadataStream.Cursor cursor, SquashfsInode inode, SquashfsEntryKind kind, bool extended)
    {
        var body = cursor.ReadBytes(kind is SquashfsEntryKind.Fifo or SquashfsEntryKind.Socket
            ? (extended ? 8 : 4)
            : (extended ? 12 : 8));
        var xattrReadOffset = 0;
        if (kind is SquashfsEntryKind.BlockDevice or SquashfsEntryKind.CharDevice)
        {
            inode.DeviceNumber = U32(body, 4);
            xattrReadOffset = 8;
        }

        if (extended)
        {
            inode.XattrIndex = InvalidIfSentinel(U32(body, xattrReadOffset));
        }
        return inode;
    }

    private int ComputeBlockCount(long size, uint fragment)
    {
        if (fragment == uint.MaxValue)
        {
            var count = (size + _superblock.BlockSize - 1) >> _superblock.BlockLog;
            if (count > MaximumBlockCount)
            {
                throw new InvalidDataException($"The SquashFS file declares {count} blocks, beyond the supported limit.");
            }

            return (int)count;
        }

        var whole = size >> _superblock.BlockLog;
        if (whole > MaximumBlockCount)
        {
            throw new InvalidDataException($"The SquashFS file declares {whole} blocks, beyond the supported limit.");
        }

        return (int)whole;
    }

    private void ReadXattrHeader(CancellationToken cancellationToken)
    {
        if (_superblock.XattrIdTableStart == ulong.MaxValue)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileExtraction.ValidateRange((long)_superblock.XattrIdTableStart, 24, (long)_superblock.BytesUsed, "SquashFS xattr id table");
        var header = new byte[24];
        _input.Position = (long)_superblock.XattrIdTableStart;
        _input.ReadExactly(header);
        XattrIdCount = U32(header, 8);
        if (XattrIdCount > 1 << 20)
        {
            throw new InvalidDataException($"The SquashFS xattr table declares {XattrIdCount} ids, beyond the supported limit.");
        }
    }

    private uint[] ReadIdTable(CancellationToken cancellationToken)
    {
        var bytes = ReadIndexAddressedTable(_superblock.IdTableStart, _superblock.IdCount * 4L, cancellationToken);
        var ids = new uint[_superblock.IdCount];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = U32(bytes, i * 4);
        }

        return ids;
    }

    private (ulong Start, uint Size, uint Uncached)[] ReadFragmentTable(CancellationToken cancellationToken)
    {
        if (_superblock.FragmentCount == 0)
        {
            return [];
        }

        var bytes = ReadIndexAddressedTable(_superblock.FragmentTableStart, _superblock.FragmentCount * (long)FragmentEntryBytes, cancellationToken);
        var fragments = new (ulong, uint, uint)[_superblock.FragmentCount];
        for (var i = 0; i < fragments.Length; i++)
        {
            fragments[i] = (U64(bytes, i * FragmentEntryBytes), U32(bytes, i * FragmentEntryBytes + 8), U32(bytes, i * FragmentEntryBytes + 12));
        }

        return fragments;
    }

    private byte[] ReadIndexAddressedTable(ulong tableStart, long uncompressedBytes, CancellationToken cancellationToken)
    {
        var indexEntries = (uncompressedBytes + SquashfsSuperblock.MetadataWindowSize - 1) / SquashfsSuperblock.MetadataWindowSize;
        using var table = new MemoryStream((int)Math.Clamp(uncompressedBytes, 0, 1L << 25));
        Span<byte> raw = stackalloc byte[8];
        for (var i = 0; i < indexEntries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var addressOffset = checked((long)tableStart + i * 8);
            FileExtraction.ValidateRange(addressOffset, 8, (long)_superblock.BytesUsed, "SquashFS table index");
            _input.Position = addressOffset;
            _input.ReadExactly(raw);
            var blockAddress = _superblock.BigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(raw)
                : BinaryPrimitives.ReadUInt64LittleEndian(raw);
            FileExtraction.ValidateRange((long)blockAddress, 2, (long)_superblock.BytesUsed, "SquashFS indexed metadata block");
            var decoded = DecodeBlockAt((long)blockAddress);
            table.Write(decoded, 0, decoded.Length);
        }

        if (table.Length < uncompressedBytes)
        {
            throw new InvalidDataException(
                $"A SquashFS index-addressed table stores {table.Length} bytes but {uncompressedBytes} were expected.");
        }

        return table.ToArray();
    }

    private byte[] DecodeBlockAt(long absolute)
    {
        Span<byte> header = stackalloc byte[2];
        _input.Position = absolute;
        _input.ReadExactly(header);
        var raw = _superblock.BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(header)
            : BinaryPrimitives.ReadUInt16LittleEndian(header);
        var stored = (raw & 0x8000) != 0;
        var size = raw & 0x7FFF;
        if (size == 0 || absolute + 2 + (long)size > (long)_superblock.BytesUsed)
        {
            throw new InvalidDataException($"The SquashFS metadata block at {absolute} declares an out-of-range size {size}.");
        }

        var payload = new byte[size];
        _input.ReadExactly(payload);
        return stored
            ? payload
            : SquashfsDecompression.Decompress(_superblock.Compression, payload, SquashfsSuperblock.MetadataWindowSize, exactOutput: false);
    }

    private string DecodeName(byte[] nameBytes)
    {
        var text = PortableUtf8(nameBytes);
        if (text.Length == 0 || text.Contains('/') || text.Contains('\0'))
        {
            throw new InvalidDataException($"The SquashFS entry name '{text}' cannot be mapped to a host path.");
        }

        return text;
    }

    private static string PortableUtf8(byte[] bytes) => PortableExtractionPaths.NameEncoding.GetString(bytes);

    private static int InvalidIfSentinel(uint value) => value == uint.MaxValue ? -1 : (int)value;

    private static long InvalidIfSentinel(long value) => value == uint.MaxValue ? -1 : value;

    private static SquashfsEntryKind MapKind(ushort rawType, out bool extended)
    {
        extended = rawType > 7;
        return (extended ? rawType - 7 : rawType) switch
        {
            1 => SquashfsEntryKind.Directory,
            2 => SquashfsEntryKind.RegularFile,
            3 => SquashfsEntryKind.SymbolicLink,
            4 => SquashfsEntryKind.BlockDevice,
            5 => SquashfsEntryKind.CharDevice,
            6 => SquashfsEntryKind.Fifo,
            7 => SquashfsEntryKind.Socket,
            _ => throw new InvalidDataException($"The SquashFS inode type {rawType} is unknown."),
        };
    }

    private ushort U16(ReadOnlySpan<byte> data, int offset) => _superblock.BigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
        : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private uint U32(ReadOnlySpan<byte> data, int offset) => _superblock.BigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
        : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private ulong U64(ReadOnlySpan<byte> data, int offset) => _superblock.BigEndian
        ? BinaryPrimitives.ReadUInt64BigEndian(data[offset..])
        : BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
}