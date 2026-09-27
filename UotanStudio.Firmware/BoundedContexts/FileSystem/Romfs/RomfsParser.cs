using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Romfs.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Romfs;

/// <summary>
/// Read-only parser for RomFS images (Linux <c>Documentation/filesystems/romfs.txt</c>): big-endian
/// superblock, hard links, symbolic links, directories, regular files and device entries.
/// <para>RomFS 镜像的只读解析器（Linux <c>Documentation/filesystems/romfs.txt</c>）：大端超级块、硬链接、
/// 符号链接、目录、常规文件与设备条目。</para>
/// </summary>
internal sealed class RomfsParser
{
    internal const int MaximumDepth = 64;
    internal const int MaximumEntryCount = 65_536;
    internal const int MaximumNameBytes = 4096;

    private const uint ModeMask = 0x0000_000F;
    private const int SuperblockSize = 16;
    private const int ChecksumRegionBytes = 512;
    private static readonly ReadOnlyMemory<byte> Magic = "-rom1fs-"u8.ToArray();

    private readonly Stream _input;
    private readonly long _volumeSize;
    private readonly Dictionary<long, RomfsEntry> _entriesByHeader = [];

    /// <summary>
    /// Parses the superblock and the full directory tree.
    /// <para>解析超级块与完整目录树。</para>
    /// </summary>
    /// <param name="input">Seekable RomFS image; not closed by this class. <para>可定位的 RomFS 镜像；本类不会将其关闭。</para></param>
    /// <param name="cancellationToken">Token checked throughout the walk. <para>遍历全程检查的令牌。</para></param>
    /// <exception cref="InvalidDataException">The magic, sizes, checksums, chain ordering or entry counts fail validation.
    /// <para>魔数、长度、校验和、链推进或条目数量校验失败时抛出。</para></exception>
    internal RomfsParser(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
        {
            throw new InvalidDataException("RomFS parsing requires a seekable stream.");
        }

        _input = input;
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Length < SuperblockSize + 16)
        {
            throw new InvalidDataException("The RomFS image is shorter than the superblock.");
        }

        Span<byte> magic = stackalloc byte[8];
        input.Position = 0;
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic.Span))
        {
            throw new InvalidDataException("The RomFS magic '-rom1fs-' is missing.");
        }

        Span<byte> volumeSizeBytes = stackalloc byte[4];
        input.Position = 8;
        input.ReadExactly(volumeSizeBytes);
        _volumeSize = BinaryPrimitives.ReadUInt32BigEndian(volumeSizeBytes);
        if (_volumeSize < SuperblockSize + 16 || _volumeSize > input.Length)
        {
            throw new InvalidDataException($"The RomFS volume size {_volumeSize} is outside the image bounds.");
        }

        if (_volumeSize % 4 != 0)
        {
            throw new InvalidDataException("The RomFS volume size is not a multiple of four bytes.");
        }

        var checksumRegion = (int)Math.Min(_volumeSize, ChecksumRegionBytes);
        var sum = 0U;
        for (var offset = 0; offset < checksumRegion; offset += 4)
        {
            sum += ReadUInt32BE(offset);
        }

        if (sum != 0)
        {
            throw new InvalidDataException("The RomFS superblock checksum does not cover the first bytes of the volume.");
        }

        Name = ReadVolumeName();
        var rootHeaderOffset = SuperblockSize + RoundTo16((long)Name.Length + 1);
        Root = ReadEntry(rootHeaderOffset, "", 0, cancellationToken);
        if (Root.Kind != RomfsEntryKind.Directory)
        {
            throw new InvalidDataException("The RomFS root entry is not a directory.");
        }

        foreach (var leaf in EnumerateFiles())
        {
            TotalDataBytes = checked(TotalDataBytes + ResolveData(leaf).Size);
        }
    }

    /// <summary>Volume name decoded from the superblock. <para>从超级块解码的卷名。</para></summary>
    internal string Name { get; }

    /// <summary>Accessible volume size in bytes. <para>可访问的卷大小（字节）。</para></summary>
    internal long VolumeSize => _volumeSize;

    /// <summary>Root directory entry. <para>根目录条目。</para></summary>
    internal RomfsEntry Root { get; }

    /// <summary>Total byte size of every extractable entry (files, links and hard links).
    /// <para>所有可解出条目（文件、符号链接与硬链接）的总字节数。</para></summary>
    internal long TotalDataBytes { get; private set; }

    /// <summary>Walks the tree and yields every extractable entry with its image path.
    /// <para>遍历目录树，产出全部可解出条目及其镜像路径。</para></summary>
    /// <exception cref="OperationCanceledException">The stored cancellation token was signaled.
    /// <para>保存的取消令牌被触发。</para></exception>
    internal IReadOnlyList<RomfsEntry> EnumerateFiles()
    {
        var results = new List<RomfsEntry>();
        Collect(Root, results);
        return results;

        static void Collect(RomfsEntry entry, List<RomfsEntry> sink)
        {
            switch (entry.Kind)
            {
                case RomfsEntryKind.RegularFile:
                case RomfsEntryKind.SymbolicLink:
                case RomfsEntryKind.HardLink:
                    sink.Add(entry);
                    break;
            }

            foreach (var child in entry.Children)
            {
                Collect(child, sink);
            }
        }
    }

    /// <summary>Reads the raw data bytes of one entry; hard links follow their target.
    /// <para>读取单个条目的原始数据字节；硬链接会跟踪其目标。</para></summary>
    /// <param name="entry">Entry to read; directory entries yield empty arrays. <para>待读取条目；目录条目返回空数组。</para></param>
    /// <exception cref="InvalidDataException">The data region lies outside the volume. <para>数据区超出卷边界。</para></exception>
    internal byte[] ReadEntryBytes(RomfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (offset, size) = ResolveData(entry);
        if (size == 0)
        {
            return [];
        }

        FileExtraction.ValidateRange(offset, size, _volumeSize, "RomFS entry data");
        var buffer = new byte[size];
        _input.Position = offset;
        _input.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Extracts the volume tree into a directory. Symbolic links and hard links are written as regular
    /// files carrying their target bytes; device, socket and FIFO entries are skipped.
    /// <para>将卷目录树解出到本地目录。符号链接与硬链接以携带目标字节的普通文件写出；设备、套接字与
    /// FIFO 条目被跳过。</para>
    /// </summary>
    /// <param name="outputDirectory">Destination root. <para>目标根目录。</para></param>
    /// <param name="overwrite">Replace existing destination files when <see langword="true"/>. <para>为 <see langword="true"/> 时覆盖已存在的目标文件。</para></param>
    /// <param name="maximumOutputBytes">Cumulative write budget across the whole extraction. <para>整个解包过程的累计写入预算。</para></param>
    /// <param name="byteProgress">Receives cumulative processed/total bytes; may be <see langword="null"/>. <para>接收累计已处理/总字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Token checked per entry and per chunk. <para>按条目和按块检查的令牌。</para></param>
    /// <returns>The image paths of the written files. <para>已写出文件的镜像路径列表。</para></returns>
    /// <exception cref="InvalidDataException">An entry escapes the output directory or exceeds the budget.
    /// <para>条目逃逸输出目录或超出预算时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal IReadOnlyList<string> ExtractToDirectory(
        string outputDirectory,
        bool overwrite,
        long maximumOutputBytes,
        IProgress<(long Processed, long Total)>? byteProgress,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var total = TotalDataBytes;
        var processed = 0L;
        ExtractNode(Root);
        byteProgress?.Report((processed, total));
        return results;

        void ExtractNode(RomfsEntry entry)
        {
            foreach (var child in entry.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (child.Kind)
                {
                    case RomfsEntryKind.Directory:
                        Directory.CreateDirectory(FileExtraction.ResolvePath(outputDirectory, child.Path));
                        ExtractNode(child);
                        break;
                    case RomfsEntryKind.RegularFile:
                    case RomfsEntryKind.SymbolicLink:
                    case RomfsEntryKind.HardLink:
                        WriteLeaf(child);
                        results.Add(child.Path);
                        break;
                }
            }
        }

        void WriteLeaf(RomfsEntry entry)
        {
            var target = FileExtraction.ResolvePath(outputDirectory, entry.Path);
            var (offset, size) = ResolveData(entry);
            var remaining = Math.Max(0, maximumOutputBytes - processed);
            if (size > remaining)
            {
                throw new InvalidDataException(
                    $"RomFS extraction of '{entry.Path}' would exceed the {maximumOutputBytes}-byte output budget.");
            }

            var chunk = new byte[Math.Min(size, 81920)];
            FileExtraction.WriteAtomic(target, stream =>
            {
                _input.Position = offset;
                var written = 0L;
                while (written < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = _input.Read(chunk, 0, (int)Math.Min(chunk.Length, size - written));
                    if (read <= 0)
                    {
                        throw new InvalidDataException($"RomFS entry '{entry.Path}' ends before its declared size.");
                    }

                    stream.Write(chunk, 0, read);
                    written += read;
                }
            }, overwrite, cancellationToken);
            processed = checked(processed + size);
            byteProgress?.Report((processed, total));
        }
    }

    private (long Offset, long Size) ResolveData(RomfsEntry entry) => entry.Kind switch
    {
        RomfsEntryKind.RegularFile or RomfsEntryKind.SymbolicLink => (entry.DataOffset, entry.Size),
        RomfsEntryKind.HardLink => ResolveData(_entriesByHeader[entry.LinkTargetOffset]),
        _ => (-1, 0),
    };

    private RomfsEntry ReadEntry(long headerOffset, string parentPath, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDepth)
        {
            throw new InvalidDataException("The RomFS directory tree exceeds the supported depth.");
        }

        if (_entriesByHeader.Count >= MaximumEntryCount)
        {
            throw new InvalidDataException("The RomFS volume stores more entries than the supported limit.");
        }

        if (headerOffset % 16 != 0 || headerOffset + 16 > _volumeSize)
        {
            throw new InvalidDataException($"The RomFS file header at {headerOffset} is outside the volume.");
        }

        var nextRaw = ReadUInt32BE(headerOffset);
        var specInfo = ReadUInt32BE(headerOffset + 4);
        var size = ReadUInt32BE(headerOffset + 8);
        var storedChecksum = ReadUInt32BE(headerOffset + 12);
        var type = (int)(nextRaw & 0x0000_0007);
        var kind = type switch
        {
            0 => RomfsEntryKind.HardLink,
            1 => RomfsEntryKind.Directory,
            2 => RomfsEntryKind.RegularFile,
            3 => RomfsEntryKind.SymbolicLink,
            4 => RomfsEntryKind.BlockDevice,
            5 => RomfsEntryKind.CharDevice,
            6 => RomfsEntryKind.Socket,
            7 => RomfsEntryKind.Fifo,
            _ => throw new UnreachableException(),
        };

        var nameBytes = ReadPaddedName(headerOffset + 16, out var nameRegionBytes);
        var name = DecodeName(nameBytes);
        var dataOffset = checked(headerOffset + 16 + nameRegionBytes);
        ValidateChecksum(headerOffset, 16 + nameRegionBytes, storedChecksum);

        if (kind is RomfsEntryKind.RegularFile or RomfsEntryKind.SymbolicLink)
        {
            FileExtraction.ValidateRange(dataOffset, size, _volumeSize, "RomFS entry data");
        }

        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        var entry = new RomfsEntry
        {
            Kind = kind,
            Name = name,
            Path = path,
            Size = size,
            HeaderOffset = headerOffset,
            DataOffset = dataOffset,
            LinkTargetOffset = kind == RomfsEntryKind.HardLink ? specInfo & ~ModeMask : -1,
            Device = kind is RomfsEntryKind.BlockDevice or RomfsEntryKind.CharDevice
                ? (specInfo >> 16, (specInfo >> 8) & 0xFF)
                : (0, 0),
        };
        _entriesByHeader[headerOffset] = entry;

        switch (kind)
        {
            case RomfsEntryKind.Directory:
                ReadDirectoryChildren(entry, specInfo & ~ModeMask, depth, cancellationToken);
                break;
            case RomfsEntryKind.HardLink when name is not ("." or ".."):
                ResolveHardLinkTarget(entry, depth, cancellationToken);
                break;
        }

        return entry;
    }

    private void ReadDirectoryChildren(RomfsEntry directory, long firstChildOffset, int depth, CancellationToken cancellationToken)
    {
        if (firstChildOffset <= directory.HeaderOffset || firstChildOffset + 16 > _volumeSize)
        {
            throw new InvalidDataException(
                $"The RomFS directory at {directory.HeaderOffset} has an invalid first-entry offset {firstChildOffset}.");
        }

        var offset = firstChildOffset;
        while (offset != 0)
        {
            var entry = ReadEntry(offset, directory.Path, depth + 1, cancellationToken);
            var next = ReadUInt32BE(offset) & ~ModeMask;
            if (next != 0 && next <= offset)
            {
                throw new InvalidDataException($"The RomFS entry chain at {offset} does not advance.");
            }

            if (entry.Name is "." or "..")
            {
                _entriesByHeader.Remove(offset);
                offset = next;
                continue;
            }

            directory.Children.Add(entry);
            offset = next;
        }
    }

    private void ResolveHardLinkTarget(RomfsEntry link, int depth, CancellationToken cancellationToken)
    {
        var target = link.LinkTargetOffset;
        if (target % 16 != 0 || target + 16 > _volumeSize)
        {
            throw new InvalidDataException($"The RomFS hard link '{link.Path}' points outside the volume.");
        }

        var targetKind = ReadUInt32BE(target) & 0x0000_0007;
        if (targetKind is 0 or 1)
        {
            throw new InvalidDataException($"The RomFS hard link '{link.Path}' points at an invalid target.");
        }

        if (!_entriesByHeader.TryGetValue(target, out var referenced))
        {
            referenced = ReadEntry(target, link.Path, depth, cancellationToken);
            _entriesByHeader[target] = referenced;
        }
    }

    private byte[] ReadPaddedName(long nameOffset, out int nameRegionBytes)
    {
        if (nameOffset >= _volumeSize)
        {
            throw new InvalidDataException("The RomFS entry name starts outside the volume.");
        }

        var collected = new List<byte>();
        var terminator = -1;
        var position = nameOffset;
        while (position < _volumeSize)
        {
            var chunk = new byte[(int)Math.Min(16, _volumeSize - position)];
            _input.Position = position;
            _input.ReadExactly(chunk);
            var nul = Array.IndexOf(chunk, (byte)0);
            collected.AddRange(nul >= 0 ? chunk[..nul] : chunk);
            if (nul >= 0)
            {
                terminator = (int)(position - nameOffset) + nul;
                break;
            }

            position += 16;
            if (collected.Count > MaximumNameBytes)
            {
                throw new InvalidDataException("The RomFS entry name exceeds the supported length.");
            }
        }

        if (terminator < 0)
        {
            throw new InvalidDataException("The RomFS entry name is not terminated inside the volume.");
        }

        nameRegionBytes = checked((int)RoundTo16((long)terminator + 1));
        return collected.ToArray();
    }

    private void ValidateChecksum(long regionOffset, int regionBytes, uint storedChecksum)
    {
        var sum = 0U;
        for (var offset = 0; offset < regionBytes; offset += 4)
        {
            sum += ReadUInt32BE(checked(regionOffset + offset));
        }

        if (sum != 0)
        {
            throw new InvalidDataException($"The RomFS metadata checksum at {regionOffset} is corrupt.");
        }
    }

    private static string DecodeName(byte[] nameBytes) =>
        Encoding.UTF8.GetString(nameBytes).Replace('\0', ' ');

    private uint ReadUInt32BE(long offset)
    {
        if (offset < 0 || offset + 4 > _volumeSize)
        {
            throw new InvalidDataException($"The RomFS read at {offset} exceeds the volume.");
        }

        Span<byte> buffer = stackalloc byte[4];
        _input.Position = offset;
        _input.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private string ReadVolumeName()
    {
        var collected = new List<byte>();
        var position = (long)SuperblockSize;
        _input.Position = position;
        while (position < _volumeSize)
        {
            var b = _input.ReadByte();
            if (b < 0)
            {
                break;
            }

            position++;
            if (b == 0)
            {
                return Encoding.UTF8.GetString(collected.ToArray());
            }

            collected.Add((byte)b);
            if (collected.Count > MaximumNameBytes)
            {
                throw new InvalidDataException("The RomFS volume name exceeds the supported length.");
            }
        }

        throw new InvalidDataException("The RomFS volume name is not terminated inside the volume.");
    }

    private static long RoundTo16(long value) => (value + 15) & ~15L;
}