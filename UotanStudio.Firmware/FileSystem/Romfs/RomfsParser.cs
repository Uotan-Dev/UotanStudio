using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.FileSystem.Romfs;

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
    private const int MetadataWindowBytes = 64 * 1024;
    private const int CopyChunkBytes = 81_920;
    private static readonly ReadOnlyMemory<byte> Magic = "-rom1fs-"u8.ToArray();

    private readonly Stream _input;
    private readonly long _volumeSize;
    private readonly Dictionary<long, RomfsEntry> _entriesByHeader = [];
    private readonly byte[] _window = new byte[MetadataWindowBytes];
    private long _windowStart = -1;
    private int _windowLength;

    /// <summary>
    /// Parses the superblock and the full directory tree.
    /// <para>解析超级块与完整目录树。</para>
    /// </summary>
    /// <param name="input">Seekable RomFS image; not closed by this class. <para>可定位的 RomFS 镜像；本类不会将其关闭。</para></param>
    /// <param name="cancellationToken">Token checked throughout the walk. <para>遍历全程检查的令牌。</para></param>
    /// <exception cref="InvalidDataException">The input stream is not seekable. <para>输入流不可定位。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The magic, sizes, checksums, chain ordering or entry counts fail validation.
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
            throw new FileSystemCorruptedException("The RomFS image is shorter than the superblock.");
        }

        Span<byte> magic = stackalloc byte[8];
        input.Position = 0;
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic.Span))
        {
            throw new FileSystemCorruptedException("The RomFS magic '-rom1fs-' is missing.");
        }

        Span<byte> volumeSizeBytes = stackalloc byte[4];
        input.Position = 8;
        input.ReadExactly(volumeSizeBytes);
        _volumeSize = BinaryPrimitives.ReadUInt32BigEndian(volumeSizeBytes);
        if (_volumeSize < SuperblockSize + 16 || _volumeSize > input.Length)
        {
            throw new FileSystemCorruptedException($"The RomFS volume size {_volumeSize} is outside the image bounds.");
        }

        if (_volumeSize % 4 != 0)
        {
            throw new FileSystemCorruptedException("The RomFS volume size is not a multiple of four bytes.");
        }

        var checksumRegion = (int)Math.Min(_volumeSize, ChecksumRegionBytes);
        var sum = 0U;
        for (var offset = 0; offset < checksumRegion; offset += 4)
        {
            sum += ReadUInt32BE(offset);
        }

        if (sum != 0)
        {
            throw new FileSystemCorruptedException("The RomFS superblock checksum does not cover the first bytes of the volume.");
        }

        Name = ReadVolumeName();
        var rootHeaderOffset = SuperblockSize + RoundTo16((long)Name.Length + 1);
        Root = ReadEntry(rootHeaderOffset, "", 0, cancellationToken);
        if (Root.Kind != RomfsEntryKind.Directory)
        {
            throw new FileSystemCorruptedException("The RomFS root entry is not a directory.");
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
    /// <exception cref="FileSystemCorruptedException">The data region lies outside the volume. <para>数据区超出卷边界。</para></exception>
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
    /// <exception cref="InvalidDataException">An entry escapes the output directory.
    /// <para>条目逃逸输出目录时抛出。</para></exception>
    /// <exception cref="FileSystemCorruptedException">An entry exceeds the budget or ends before its declared size.
    /// <para>条目超出预算或在声明大小前提前结束时抛出。</para></exception>
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
        var throttle = new ProgressThrottle(total, byteProgress, cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyChunkBytes);
        try
        {
            throttle.Report((0, total));
            ExtractNode(Root);
            throttle.Complete();
            return results;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

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
                throw new FileSystemCorruptedException(
                    $"RomFS extraction of '{entry.Path}' would exceed the {maximumOutputBytes}-byte output budget.");
            }

            FileExtraction.WriteAtomic(target, stream =>
            {
                _input.Position = offset;
                var written = 0L;
                while (written < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = _input.Read(buffer, 0, (int)Math.Min((long)buffer.Length, size - written));
                    if (read <= 0)
                    {
                        throw new FileSystemCorruptedException($"RomFS entry '{entry.Path}' ends before its declared size.");
                    }

                    stream.Write(buffer, 0, read);
                    written += read;
                }
            }, overwrite, cancellationToken);
            processed = checked(processed + size);
            throttle.Report((processed, total));
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
            throw new FileSystemCorruptedException("The RomFS directory tree exceeds the supported depth.");
        }

        if (_entriesByHeader.Count >= MaximumEntryCount)
        {
            throw new FileSystemCorruptedException("The RomFS volume stores more entries than the supported limit.");
        }

        if (headerOffset % 16 != 0 || headerOffset + 16 > _volumeSize)
        {
            throw new FileSystemCorruptedException($"The RomFS file header at {headerOffset} is outside the volume.");
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

        var name = ReadEntryName(headerOffset + 16, out var nameRegionBytes);
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
            throw new FileSystemCorruptedException(
                $"The RomFS directory at {directory.HeaderOffset} has an invalid first-entry offset {firstChildOffset}.");
        }

        var offset = firstChildOffset;
        while (offset != 0)
        {
            var entry = ReadEntry(offset, directory.Path, depth + 1, cancellationToken);
            var next = ReadUInt32BE(offset) & ~ModeMask;
            if (next != 0 && next <= offset)
            {
                throw new FileSystemCorruptedException($"The RomFS entry chain at {offset} does not advance.");
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
            throw new FileSystemCorruptedException($"The RomFS hard link '{link.Path}' points outside the volume.");
        }

        var targetKind = ReadUInt32BE(target) & 0x0000_0007;
        if (targetKind is 0 or 1)
        {
            throw new FileSystemCorruptedException($"The RomFS hard link '{link.Path}' points at an invalid target.");
        }

        if (!_entriesByHeader.TryGetValue(target, out var referenced))
        {
            referenced = ReadEntry(target, link.Path, depth, cancellationToken);
            _entriesByHeader[target] = referenced;
        }
    }

    /// <summary>
    /// Reads the NUL-terminated entry name at <paramref name="nameOffset"/> and returns it as text, reporting
    /// the 16-byte-aligned region the name occupies including its terminator.
    /// <para>读取 <paramref name="nameOffset"/> 处 NUL 结尾的条目名并返回文本，同时报告名称连同终止符占用的
    /// 16 字节对齐区间。</para>
    /// </summary>
    /// <param name="nameOffset">First name byte inside the volume. <para>卷内的名称首字节。</para></param>
    /// <param name="nameRegionBytes">Receives the aligned name region length.
    /// <para>接收对齐后的名称区间长度。</para></param>
    /// <returns>The decoded name without the terminator. <para>不含终止符的解码名称。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The name is unterminated or exceeds the supported length.
    /// <para>名称未终止或超出支持长度时抛出。</para></exception>
    private string ReadEntryName(long nameOffset, out int nameRegionBytes)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumNameBytes);
        try
        {
            var length = ReadNameBytes(nameOffset, buffer.AsSpan(0, MaximumNameBytes));
            nameRegionBytes = checked((int)RoundTo16(length + 1L));
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads name bytes up to the terminating NUL through the buffered metadata window.
    /// <para>通过缓冲的元数据窗口读取名称字节直到终止 NUL。</para>
    /// </summary>
    /// <param name="nameOffset">First name byte inside the volume. <para>卷内的名称首字节。</para></param>
    /// <param name="destination">Buffer receiving the bytes before the terminator.
    /// <para>接收终止符之前字节的缓冲区。</para></param>
    /// <returns>The number of name bytes stored. <para>保存的名称字节数。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The name starts outside the volume, is unterminated, or
    /// exceeds <see cref="MaximumNameBytes"/>. <para>名称起点在卷外、未终止或超出
    /// <see cref="MaximumNameBytes"/> 时抛出。</para></exception>
    private int ReadNameBytes(long nameOffset, Span<byte> destination)
    {
        if (nameOffset >= _volumeSize)
        {
            throw new FileSystemCorruptedException("The RomFS entry name starts outside the volume.");
        }

        var filled = 0;
        var position = nameOffset;
        while (position < _volumeSize)
        {
            EnsureWindow(position, 1);
            var start = (int)(position - _windowStart);
            var available = _window.AsSpan(start, _windowLength - start);
            var nul = available.IndexOf((byte)0);
            var take = nul >= 0 ? nul : available.Length;
            if (filled + take > destination.Length)
            {
                throw new FileSystemCorruptedException("The RomFS entry name exceeds the supported length.");
            }

            available[..take].CopyTo(destination[filled..]);
            filled += take;
            if (nul >= 0)
            {
                return filled;
            }

            position += take;
        }

        throw new FileSystemCorruptedException("The RomFS entry name is not terminated inside the volume.");
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
            throw new FileSystemCorruptedException($"The RomFS metadata checksum at {regionOffset} is corrupt.");
        }
    }

    /// <summary>
    /// Reads one big-endian dword through the buffered metadata window, loading a fresh 64 KiB window only
    /// when the offset falls outside the one already resident.
    /// <para>通过缓冲的元数据窗口读取一个大端双字，仅在偏移落在当前驻留窗口之外时重新装载 64 KiB 窗口。</para>
    /// </summary>
    /// <param name="offset">Absolute volume offset of the dword. <para>双字的卷内绝对偏移。</para></param>
    /// <returns>The decoded value. <para>解码后的值。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The read exceeds the volume.
    /// <para>读取超出卷边界时抛出。</para></exception>
    private uint ReadUInt32BE(long offset)
    {
        EnsureWindow(offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(_window.AsSpan((int)(offset - _windowStart), 4));
    }

    private void EnsureWindow(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > _volumeSize || count > _volumeSize - offset)
        {
            throw new FileSystemCorruptedException($"The RomFS read at {offset} exceeds the volume.");
        }

        if (_windowStart >= 0 && offset >= _windowStart && offset - _windowStart <= _windowLength - count)
        {
            return;
        }

        var length = (int)Math.Min(MetadataWindowBytes, _volumeSize - offset);
        _input.Position = offset;
        _input.ReadExactly(_window.AsSpan(0, length));
        _windowStart = offset;
        _windowLength = length;
    }

    private string ReadVolumeName()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumNameBytes);
        try
        {
            var length = ReadNameBytes(SuperblockSize, buffer.AsSpan(0, MaximumNameBytes));
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long RoundTo16(long value) => (value + 15) & ~15L;
}