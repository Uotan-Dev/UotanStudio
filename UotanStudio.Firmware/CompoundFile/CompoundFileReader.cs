using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.CompoundFile;

/// <summary>
/// Bounded read-only parser for compound file (OLE2/CFB) containers, the container used by Qualcomm QCN NV backup files among others. Every offset, sector index, chain length and declared size is bounds-checked and chain walks are capped. Supports version 3 and version 4 containers including the mini FAT subsystem.
/// <para>复合文档（OLE2/CFB）容器的有界只读解析器，Qualcomm QCN NV 备份文件等即使用该容器。所有偏移、扇区号、链长与声明大小均做上限检查，链遍历有步数上限。支持版本 3 与版本 4 容器，含 mini FAT 子系统。</para>
/// </summary>
public sealed class CompoundFileReader : IDisposable
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFD;
    private const uint NoStream = 0xFFFFFFFF;

    private static readonly byte[] Signature =
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const int HeaderLength = 512;
    private const int DirectoryEntryLength = 128;
    private const int MiniSectorSize = 64;
    private const uint MiniStreamCutoff = 0x1000;
    private const int MaxEntries = 1 << 20;

    private readonly FileStream _stream;
    private readonly uint _sectorSize;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<CompoundFileEntry> _entries;
    private readonly CompoundFileEntry _root;
    private readonly Dictionary<string, CompoundFileEntry> _entriesByPath;

    private CompoundFileReader(
        FileStream stream,
        uint sectorSize,
        uint[] fat,
        uint[] miniFat,
        List<CompoundFileEntry> entries,
        CompoundFileEntry root,
        Dictionary<string, CompoundFileEntry> entriesByPath)
    {
        _stream = stream;
        _sectorSize = sectorSize;
        _fat = fat;
        _miniFat = miniFat;
        _entries = entries;
        _root = root;
        _entriesByPath = entriesByPath;
    }

    /// <summary>
    /// Gets the container sector size in bytes (512 for version 3, 4096 for version 4).
    /// <para>获取容器扇区大小（字节）：版本 3 为 512，版本 4 为 4096。</para>
    /// </summary>
    /// <value>
    /// Sector size in bytes.
    /// <para>扇区大小（字节）。</para>
    /// </value>
    public uint SectorSize => _sectorSize;

    /// <summary>
    /// Opens and parses the compound file at the given path.
    /// <para>打开并解析指定路径的复合文档。</para>
    /// </summary>
    /// <param name="path">
    /// The compound file path.
    /// <para>复合文档文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token carried through the header and structure reads.
    /// <para>取消令牌，贯穿头部与结构读取。</para>
    /// </param>
    /// <returns>
    /// A parsed reader instance; must be disposed when no longer needed.
    /// <para>已解析的读取器实例；使用完毕后须释放。</para>
    /// </returns>
    /// <exception cref="InvalidCompoundFileMagicException">
    /// The file does not start with the OLE2/CFB magic or is shorter than 512 bytes.
    /// <para>文件不以 OLE2/CFB 魔数开头或短于 512 字节。</para>
    /// </exception>
    /// <exception cref="UnsupportedCompoundFileVersionException">
    /// The header declares an unsupported major version or sector shift.
    /// <para>头部声明的主版本或扇区移位不受支持。</para>
    /// </exception>
    /// <exception cref="CompoundFileCorruptedException">
    /// The structure is truncated, self-contradictory or violates a bound.
    /// <para>结构被截断、自相矛盾或违反上限。</para>
    /// </exception>
    public static async Task<CompoundFileReader> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        CompoundFileReader reader;
        try
        {
            reader = await ParseCoreAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return reader;
    }

    /// <summary>
    /// Releases the underlying file stream.
    /// <para>释放底层文件流。</para>
    /// </summary>
    public void Dispose() => _stream.Dispose();

    /// <summary>
    /// Gets all directory entries reachable from the root entry, in tree-walk order.
    /// <para>获取从根条目可达的全部目录项（按树遍历顺序）。</para>
    /// </summary>
    /// <value>
    /// Read-only list of entries; the root entry itself is included with an empty
    /// <see cref="CompoundFileEntry.FullPath"/>.
    /// <para>只读条目列表；根条目自身也在其中，其 <see cref="CompoundFileEntry.FullPath"/> 为空。</para>
    /// </value>
    public IReadOnlyList<CompoundFileEntry> Entries => _entries;

    /// <summary>
    /// Finds a directory entry by its slash-separated path from the root entry (case-insensitive).
    /// <para>按从根条目开始的斜杠分隔路径查找目录项（不区分大小写）。</para>
    /// </summary>
    /// <param name="path">
    /// The entry path, e.g. <c>00000000/default/NV_ITEM_ARRAY</c>; an empty string matches the root.
    /// <para>条目路径，例如 <c>00000000/default/NV_ITEM_ARRAY</c>；空字符串匹配根条目。</para>
    /// </param>
    /// <returns>
    /// The matching entry; <c>null</c> when no entry has that path.
    /// <para>命中的条目；未命中时为 <c>null</c>。</para>
    /// </returns>
    public CompoundFileEntry? TryGetEntryByPath(string path) =>
        _entriesByPath.TryGetValue(path, out var entry) ? entry : null;

    /// <summary>
    /// Opens the content of a stream entry as a bounded read-only seekable stream.
    /// <para>把流条目的内容以有界只读可定位流的形式打开。</para>
    /// </summary>
    /// <param name="entry">
    /// The stream entry to open.
    /// <para>要打开的流条目。</para>
    /// </param>
    /// <returns>
    /// A read-only stream whose length equals the declared stream size of the entry.
    /// <para>只读流，长度等于条目声明的流大小。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The entry is neither a stream nor the root.
    /// <para>条目不是流或根条目。</para>
    /// </exception>
    /// <exception cref="CompoundFileCorruptedException">
    /// The sector chain is shorter than the declared size, contains invalid sector indices or forms a cycle.
    /// <para>扇区链短于声明大小、包含无效扇区号或形成环。</para>
    /// </exception>
    public Stream OpenStream(CompoundFileEntry entry)
    {
        if (entry.Type is CompoundFileEntryType.Storage)
        {
            throw new ArgumentException($"Entry '{entry.FullPath}' is a storage, not a stream.", nameof(entry));
        }

        var maxSectorIndex = _fat.Length - 1;
        if (entry.StreamSize == 0)
        {
            return new CompoundFileStream(_stream, [], MiniSectorSize, 0);
        }

        if (entry.Type == CompoundFileEntryType.Root || entry.StreamSize >= MiniStreamCutoff)
        {
            var blocks = (long)((entry.StreamSize + _sectorSize - 1) / _sectorSize);
            var chain = WalkFatChain(entry.StartSector, blocks, maxSectorIndex, entry.FullPath);
            var offsets = new long[chain.Count];
            for (var i = 0; i < chain.Count; i++)
            {
                offsets[i] = ((long)chain[i] + 1) * _sectorSize;
            }

            return new CompoundFileStream(_stream, offsets, (int)_sectorSize, entry.StreamSize);
        }

        var miniBlocks = (long)((entry.StreamSize + MiniSectorSize - 1) / MiniSectorSize);
        var miniChain = WalkMiniFatChain(entry.StartSector, miniBlocks, entry.FullPath);
        var rootBlocks = (long)((_root.StreamSize + _sectorSize - 1) / _sectorSize);
        var containerChain = WalkFatChain(_root.StartSector, rootBlocks, maxSectorIndex, _root.FullPath);
        var miniOffsets = new long[miniChain.Count];
        for (var i = 0; i < miniChain.Count; i++)
        {
            var containerOffset = (long)miniChain[i] * MiniSectorSize;
            var containerBlock = containerOffset / _sectorSize;
            if (containerBlock >= containerChain.Count)
            {
                throw new CompoundFileCorruptedException($"The mini stream of '{entry.FullPath}' addresses mini sector {miniChain[i]} outside the root mini-stream container.");
            }

            miniOffsets[i] = ((long)containerChain[(int)containerBlock] + 1) * _sectorSize + (containerOffset % _sectorSize);
        }

        return new CompoundFileStream(_stream, miniOffsets, MiniSectorSize, entry.StreamSize);
    }

    /// <summary>
    /// Computes the physical file byte offset of every logical block of a stream entry (sector-sized
    /// blocks for regular streams, 64-byte blocks for mini streams). Used to patch stream bytes in
    /// place without rebuilding the container.
    /// <para>计算流条目每个逻辑块在文件中的物理字节偏移（常规流为扇区大小，mini 流为 64 字节）。
    /// 用于在不重建容器的前提下原位补丁流字节。</para>
    /// </summary>
    /// <param name="entry">
    /// The stream entry.
    /// <para>流条目。</para>
    /// </param>
    /// <returns>
    /// The stream block layout (physical offsets and logical block size).
    /// <para>流块布局（物理偏移数组与逻辑块大小）。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The entry is neither a stream nor the root, or the stream is empty.
    /// <para>条目不是流或根条目，或流为空。</para>
    /// </exception>
    /// <exception cref="CompoundFileCorruptedException">
    /// The sector chain is shorter than the declared size, contains invalid sector indices or forms a cycle.
    /// <para>扇区链短于声明大小、包含无效扇区号或形成环。</para>
    /// </exception>
    public CompoundFileStreamLayout GetStreamBlockLayout(CompoundFileEntry entry)
    {
        using var probe = OpenStream(entry);
        if (probe is CompoundFileStream compoundStream)
        {
            return new CompoundFileStreamLayout(compoundStream.BlockOffsets, compoundStream.BlockSize);
        }

        throw new InvalidOperationException("OpenStream returned an unexpected stream implementation.");
    }

    private static async Task<CompoundFileReader> ParseCoreAsync(FileStream fileStream, CancellationToken cancellationToken)
    {
        var fileLength = fileStream.Length;
        if (fileLength < HeaderLength)
        {
            throw new InvalidCompoundFileMagicException($"The stream is {fileLength} bytes long; an OLE2/CFB container requires at least {HeaderLength} bytes.");
        }

        var header = new byte[HeaderLength];
        await ReadExactAsync(fileStream, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidCompoundFileMagicException("The input does not carry the OLE2/CFB signature D0 CF 11 E0 A1 B1 1A E1.");
        }

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
        var miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56));
        if ((majorVersion != 3 || sectorShift != 9) && (majorVersion != 4 || sectorShift != 12))
        {
            throw new UnsupportedCompoundFileVersionException(majorVersion);
        }

        if (miniSectorShift != 6 || miniStreamCutoff != MiniStreamCutoff)
        {
            throw new CompoundFileCorruptedException($"Unexpected mini sector shift {miniSectorShift} or mini stream cutoff {miniStreamCutoff}; this library requires 6 and {MiniStreamCutoff}.");
        }

        var sectorSize = 1u << sectorShift;
        var maxSectorIndex = checked((long)(fileLength / sectorSize)) - 1;
        var numFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
        var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48));
        var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60));
        var numMiniFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64));
        var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68));
        var numDifatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72));

        if (numFatSectors > maxSectorIndex + 1)
        {
            throw new CompoundFileCorruptedException($"The header declares {numFatSectors} FAT sectors, which exceeds the {maxSectorIndex + 1} sectors the {fileLength}-byte file can hold.");
        }

        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76 + (4 * i)));
            AddDifatValue(fatSectors, value, maxSectorIndex, (int)Math.Min(numFatSectors, int.MaxValue));
        }

        var difatWalked = 0u;
        var difatSector = firstDifatSector;
        var difatCapacity = (int)(sectorSize / 4) - 1;
        while (difatSector != EndOfChain && difatSector != NoStream && difatWalked < numDifatSectors)
        {
            ValidateSectorIndex(difatSector, maxSectorIndex, "DIFAT");
            if (difatWalked >= maxSectorIndex + 1)
            {
                throw new CompoundFileCorruptedException("The DIFAT chain walk exceeded the sector count of the file.");
            }

            var difat = new byte[sectorSize];
            await ReadSectorAsync(fileStream, difat, difatSector, sectorSize, maxSectorIndex, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < difatCapacity; i++)
            {
                AddDifatValue(fatSectors, BinaryPrimitives.ReadUInt32LittleEndian(difat.AsSpan(4 * i)), maxSectorIndex, (int)Math.Min(numFatSectors, int.MaxValue));
            }

            difatSector = BinaryPrimitives.ReadUInt32LittleEndian(difat.AsSpan(4 * difatCapacity));
            difatWalked++;
        }

        if (fatSectors.Count < (int)Math.Min(numFatSectors, int.MaxValue))
        {
            throw new CompoundFileCorruptedException($"The DIFAT lists {fatSectors.Count} FAT sectors but the header declares {numFatSectors}.");
        }

        var fat = new uint[maxSectorIndex + 1];
        fat.AsSpan().Fill(FreeSect);
        var fatBase = 0L;
        foreach (var fatSector in fatSectors)
        {
            ValidateSectorIndex(fatSector, maxSectorIndex, "FAT");
            var sector = new byte[sectorSize];
            await ReadSectorAsync(fileStream, sector, fatSector, sectorSize, maxSectorIndex, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < sectorSize / 4 && fatBase + i <= maxSectorIndex; i++)
            {
                fat[fatBase + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(4 * (int)i));
            }

            fatBase += sectorSize / 4;
        }

        var rawEntries = await ParseDirectoryChainAsync(fileStream, fat, firstDirectorySector, sectorSize, maxSectorIndex, cancellationToken).ConfigureAwait(false);
        var rootIndex = rawEntries.FindIndex(entry => entry.Type == 5);
        if (rootIndex < 0)
        {
            throw new CompoundFileCorruptedException("The directory chain contains no root entry.");
        }

        var (entries, entriesByPath) = BuildTree(rawEntries, rootIndex, maxSectorIndex);
        var root = entries[0];

        var miniFat = await ParseMiniFatAsync(fileStream, fat, firstMiniFatSector, numMiniFatSectors, sectorSize, maxSectorIndex, root.StreamSize, cancellationToken).ConfigureAwait(false);

        return new CompoundFileReader(fileStream, sectorSize, fat, miniFat, entries, root, entriesByPath);
    }

    private static void AddDifatValue(List<uint> fatSectors, uint value, long maxSectorIndex, int maxFatSectors)
    {
        if (value is FreeSect or NoStream)
        {
            return;
        }

        ValidateSectorIndex(value, maxSectorIndex, "DIFAT");
        if (fatSectors.Count >= maxFatSectors)
        {
            throw new CompoundFileCorruptedException($"The DIFAT lists more than the declared {maxFatSectors} FAT sectors.");
        }

        fatSectors.Add(value);
    }

    private static void ValidateSectorIndex(uint sector, long maxSectorIndex, string what)
    {
        if (sector > (ulong)maxSectorIndex)
        {
            throw new CompoundFileCorruptedException($"{what} references sector {sector}, outside the 0..{maxSectorIndex} range the {maxSectorIndex + 1}-sector file provides.");
        }
    }

    private static async Task ReadSectorAsync(FileStream stream, byte[] buffer, uint sector, uint sectorSize, long maxSectorIndex, CancellationToken cancellationToken)
    {
        ValidateSectorIndex(sector, maxSectorIndex, "Structure");
        stream.Seek(((long)sector + 1) * sectorSize, SeekOrigin.Begin);
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(FileStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new CompoundFileCorruptedException($"The container ends after {read} of {buffer.Length} expected bytes at offset {stream.Position - read}.");
            }

            read += n;
        }
    }

    private static async Task<List<RawDirectoryEntry>> ParseDirectoryChainAsync(
        FileStream stream, uint[] fat, uint firstDirectorySector, uint sectorSize, long maxSectorIndex, CancellationToken cancellationToken)
    {
        var rawEntries = new List<RawDirectoryEntry>();
        if (firstDirectorySector is EndOfChain or NoStream or FreeSect)
        {
            throw new CompoundFileCorruptedException("The container declares no directory chain.");
        }

        var sector = firstDirectorySector;
        var visited = 0L;
        var sectorBuffer = new byte[sectorSize];
        while (sector != EndOfChain)
        {
            ValidateSectorIndex(sector, maxSectorIndex, "Directory");
            if (visited >= maxSectorIndex + 1 || rawEntries.Count >= MaxEntries)
            {
                throw new CompoundFileCorruptedException("The directory chain walk exceeded the bounds of the container.");
            }

            await ReadSectorAsync(stream, sectorBuffer, sector, sectorSize, maxSectorIndex, cancellationToken).ConfigureAwait(false);
            for (var slot = 0; slot < sectorSize / DirectoryEntryLength; slot++)
            {
                rawEntries.Add(ParseDirectoryEntry(sectorBuffer.AsSpan(slot * DirectoryEntryLength, DirectoryEntryLength), rawEntries.Count));
            }

            var next = fat[sector];
            if (next is FreeSect or NoStream)
            {
                throw new CompoundFileCorruptedException($"The directory chain hits a free/reserved sector marker at sector {sector}.");
            }

            sector = next;
            visited++;
        }

        return rawEntries;
    }

    private static RawDirectoryEntry ParseDirectoryEntry(ReadOnlySpan<byte> entry, int id)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(64, 2));
        var objectType = entry[66];
        if (objectType == 0)
        {
            return new RawDirectoryEntry(id, string.Empty, 0, NoStream, NoStream, NoStream, 0, 0);
        }

        if (objectType is not (1 or 2 or 5))
        {
            throw new CompoundFileCorruptedException($"Directory entry {id} declares unknown object type {objectType}.");
        }

        if (nameLength < 2 || nameLength > 64 || (nameLength & 1) != 0)
        {
            throw new CompoundFileCorruptedException($"Directory entry {id} declares invalid name length {nameLength}.");
        }

        var name = Encoding.Unicode.GetString(entry.Slice(0, nameLength - 2));
        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new CompoundFileCorruptedException($"Directory entry {id} carries the name '{name}' containing the path separator '/'.");
        }

        var leftSibling = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(68, 4));
        var rightSibling = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(72, 4));
        var child = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(76, 4));
        var startSector = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(116, 4));
        var streamSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(120, 4));
        return new RawDirectoryEntry(id, name, objectType, leftSibling, rightSibling, child, startSector, streamSize);
    }

    private static (List<CompoundFileEntry> Entries, Dictionary<string, CompoundFileEntry> ByPath) BuildTree(
        List<RawDirectoryEntry> rawEntries, int rootIndex, long maxSectorIndex)
    {
        var count = rawEntries.Count;
        uint ValidatePointer(uint pointer)
        {
            if (pointer is NoStream or FreeSect or EndOfChain)
            {
                return NoStream;
            }

            if (pointer >= (ulong)count)
            {
                throw new CompoundFileCorruptedException($"A directory tree pointer references entry {pointer}, outside the {count} parsed directory entries.");
            }

            return pointer;
        }

        var visited = new HashSet<int>();
        var entries = new List<CompoundFileEntry>(count);
        var byPath = new Dictionary<string, CompoundFileEntry>(count, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(int Id, string Path, string ParentPath)>();

        var root = rawEntries[rootIndex];
        var rootEntry = new CompoundFileEntry(root.Id, root.Name, string.Empty, CompoundFileEntryType.Root, root.StreamSize, root.StartSector);
        entries.Add(rootEntry);
        byPath[string.Empty] = rootEntry;
        visited.Add(root.Id);
        pending.Push((root.Id, string.Empty, string.Empty));

        while (pending.Count > 0)
        {
            var (currentId, currentPath, parentPath) = pending.Pop();
            var current = rawEntries[currentId];
            var left = ValidatePointer(current.LeftSibling);
            var right = ValidatePointer(current.RightSibling);
            var child = current.Type is 1 or 5 ? ValidatePointer(current.Child) : NoStream;

            foreach (var sibling in (uint[])[left, right])
            {
                if (sibling == NoStream || !visited.Add((int)sibling))
                {
                    continue;
                }

                var raw = rawEntries[(int)sibling];
                var type = raw.Type == 1 ? CompoundFileEntryType.Storage : CompoundFileEntryType.Stream;
                var fullPath = parentPath.Length == 0 ? raw.Name : parentPath + "/" + raw.Name;
                var entry = new CompoundFileEntry(raw.Id, raw.Name, fullPath, type, raw.StreamSize, raw.StartSector);
                entries.Add(entry);
                if (!byPath.TryAdd(fullPath, entry))
                {
                    throw new CompoundFileCorruptedException($"Two directory entries share the path '{fullPath}'.");
                }

                pending.Push(((int)sibling, fullPath, parentPath));
            }

            if (child != NoStream && visited.Add((int)child))
            {
                var raw = rawEntries[(int)child];
                var type = raw.Type == 1 ? CompoundFileEntryType.Storage : CompoundFileEntryType.Stream;
                var fullPath = currentPath.Length == 0 ? raw.Name : currentPath + "/" + raw.Name;
                var entry = new CompoundFileEntry(raw.Id, raw.Name, fullPath, type, raw.StreamSize, raw.StartSector);
                entries.Add(entry);
                if (!byPath.TryAdd(fullPath, entry))
                {
                    throw new CompoundFileCorruptedException($"Two directory entries share the path '{fullPath}'.");
                }

                pending.Push(((int)child, fullPath, currentPath));
            }
        }

        return (entries, byPath);
    }

    private static async Task<uint[]> ParseMiniFatAsync(
        FileStream stream, uint[] fat, uint firstMiniFatSector, uint numMiniFatSectors, uint sectorSize, long maxSectorIndex, long rootStreamSize, CancellationToken cancellationToken)
    {
        if (firstMiniFatSector is EndOfChain or NoStream or FreeSect || numMiniFatSectors == 0)
        {
            return [];
        }

        var maxMiniSectors = (long)((rootStreamSize + MiniSectorSize - 1) / MiniSectorSize);
        var entriesPerSector = (int)(sectorSize / 4);
        if (numMiniFatSectors > (ulong)(maxSectorIndex + 1))
        {
            throw new CompoundFileCorruptedException($"The header declares {numMiniFatSectors} mini FAT sectors, exceeding the bounds of the container.");
        }

        var maxMiniFatEntries = maxMiniSectors + 1 + entriesPerSector;
        var miniFat = new uint[Math.Min((long)numMiniFatSectors * entriesPerSector, maxMiniFatEntries)];
        var filled = 0L;
        var sector = firstMiniFatSector;
        var walked = 0L;
        var buffer = new byte[sectorSize];
        while (sector != EndOfChain)
        {
            ValidateSectorIndex(sector, maxSectorIndex, "Mini FAT");
            if (walked >= numMiniFatSectors)
            {
                throw new CompoundFileCorruptedException("The mini FAT chain is longer than the declared mini FAT sector count.");
            }

            await ReadSectorAsync(stream, buffer, sector, sectorSize, maxSectorIndex, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < entriesPerSector && filled < miniFat.Length; i++)
            {
                miniFat[filled++] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4 * i));
            }

            var next = fat[sector];
            if (next is FreeSect or NoStream)
            {
                throw new CompoundFileCorruptedException($"The mini FAT chain hits a free/reserved sector marker at sector {sector}.");
            }

            sector = next;
            walked++;
        }

        return miniFat;
    }

    private List<uint> WalkFatChain(uint startSector, long requiredBlocks, long maxSectorIndex, string owner)
    {
        var chain = new List<uint>((int)Math.Min(requiredBlocks + 1, int.MaxValue));
        var sector = startSector;
        while (sector != EndOfChain)
        {
            if (sector is FreeSect or NoStream)
            {
                throw new CompoundFileCorruptedException($"The FAT chain of '{owner}' hits a free/reserved sector marker after {chain.Count} blocks.");
            }

            ValidateSectorIndex(sector, maxSectorIndex, $"Chain of '{owner}'");
            chain.Add(sector);
            if (chain.Count > requiredBlocks)
            {
                throw new CompoundFileCorruptedException($"The FAT chain of '{owner}' is longer than its declared size requires.");
            }

            sector = _fat[sector];
        }

        if (chain.Count < requiredBlocks)
        {
            throw new CompoundFileCorruptedException($"The FAT chain of '{owner}' ends after {chain.Count} blocks but {requiredBlocks} are required by the declared size.");
        }

        return chain;
    }

    private List<uint> WalkMiniFatChain(uint startMiniSector, long requiredBlocks, string owner)
    {
        var chain = new List<uint>((int)Math.Min(requiredBlocks + 1, int.MaxValue));
        var miniSector = startMiniSector;
        while (miniSector != EndOfChain)
        {
            if (miniSector is FreeSect or NoStream)
            {
                throw new CompoundFileCorruptedException($"The mini FAT chain of '{owner}' hits a free/reserved sector marker after {chain.Count} blocks.");
            }

            if (miniSector >= (ulong)_miniFat.Length)
            {
                throw new CompoundFileCorruptedException($"The mini FAT chain of '{owner}' references mini sector {miniSector} outside the parsed mini FAT.");
            }

            chain.Add(miniSector);
            if (chain.Count > requiredBlocks)
            {
                throw new CompoundFileCorruptedException($"The mini FAT chain of '{owner}' is longer than its declared size requires.");
            }

            miniSector = _miniFat[miniSector];
        }

        if (chain.Count < requiredBlocks)
        {
            throw new CompoundFileCorruptedException($"The mini FAT chain of '{owner}' ends after {chain.Count} blocks but {requiredBlocks} are required by the declared size.");
        }

        return chain;
    }

    private readonly record struct RawDirectoryEntry(
        int Id, string Name, int Type, uint LeftSibling, uint RightSibling, uint Child, uint StartSector, long StreamSize);
}

/// <summary>
/// Read-only, seekable view over one stream of a compound file; positions map through the physical
/// block offsets computed by <see cref="CompoundFileReader.OpenStream"/>.
/// <para>复合文档单个流的只读可定位视图；位置经 <see cref="CompoundFileReader.OpenStream"/> 计算出的
/// 物理块偏移映射。</para>
/// </summary>
public sealed class CompoundFileStream : Stream
{
    private readonly FileStream _baseStream;
    private readonly long[] _blockOffsets;
    private readonly int _blockSize;
    private long _position;

    internal CompoundFileStream(FileStream baseStream, long[] blockOffsets, int blockSize, long length)
    {
        _baseStream = baseStream;
        _blockOffsets = blockOffsets;
        _blockSize = blockSize;
        Length = length;
    }

    internal long[] BlockOffsets => _blockOffsets;

    internal int BlockSize => _blockSize;

    /// <summary>
    /// Gets the stream length in bytes as declared by the directory entry.
    /// <para>获取目录项声明的流长度（字节）。</para>
    /// </summary>
    /// <value>
    /// Stream length in bytes.
    /// <para>流长度（字节）。</para>
    /// </value>
    public override long Length { get; }

    /// <summary>
    /// Gets or sets the current position inside the stream.
    /// <para>获取或设置流内的当前位置。</para>
    /// </summary>
    /// <value>
    /// The current position, always within 0..<see cref="Length"/>.
    /// <para>当前位置，始终处于 0..<see cref="Length"/> 范围内。</para>
    /// </value>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The position being set is negative or beyond the stream length.
    /// <para>设置的位置为负数或超出流长度。</para>
    /// </exception>
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the stream supports reading (always <c>true</c>).
    /// <para>获取流是否支持读取（始终为 <c>true</c>）。</para>
    /// </summary>
    /// <value>
    /// Always <c>true</c>. <para>始终为 <c>true</c>。</para>
    /// </value>
    public override bool CanRead => true;

    /// <summary>
    /// Gets a value indicating whether the stream supports seeking (always <c>true</c>).
    /// <para>获取流是否支持定位（始终为 <c>true</c>）。</para>
    /// </summary>
    /// <value>
    /// Always <c>true</c>. <para>始终为 <c>true</c>。</para>
    /// </value>
    public override bool CanSeek => true;

    /// <summary>
    /// Gets a value indicating whether the stream supports writing (always <c>false</c>).
    /// <para>获取流是否支持写入（始终为 <c>false</c>）。</para>
    /// </summary>
    /// <value>
    /// Always <c>false</c>. <para>始终为 <c>false</c>。</para>
    /// </value>
    public override bool CanWrite => false;

    /// <summary>
    /// Not supported; compound file streams are read-only.
    /// <para>不支持；复合文档流为只读。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Always thrown.
    /// <para>总是抛出。</para>
    /// </exception>
    public override void Flush() => throw new NotSupportedException();

    /// <summary>
    /// Not supported; compound file streams are read-only.
    /// <para>不支持；复合文档流为只读。</para>
    /// </summary>
    /// <param name="value">Ignored length; <see cref="Length"/> is fixed to the directory-entry size captured when the stream was opened. <para>被忽略的长度；<see cref="Length"/> 固定为打开流时记录的目录项大小。</para></param>
    /// <exception cref="NotSupportedException">
    /// Always thrown.
    /// <para>总是抛出。</para>
    /// </exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Not supported; compound file streams are read-only.
    /// <para>不支持；复合文档流为只读。</para>
    /// </summary>
    /// <param name="buffer">Ignored source array; no bytes are read from it. <para>被忽略的源数组；不会从中读取字节。</para></param>
    /// <param name="offset">Ignored index into <paramref name="buffer"/>; it is not validated. <para>被忽略的 <paramref name="buffer"/> 内索引；不会对其校验。</para></param>
    /// <param name="count">Ignored byte count; it is not validated. <para>被忽略的字节数量；不会对其校验。</para></param>
    /// <exception cref="NotSupportedException">
    /// Always thrown.
    /// <para>总是抛出。</para>
    /// </exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Reads a sequence of bytes from the current position.
    /// <para>从当前位置读取一段字节。</para>
    /// </summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">Destination offset. <para>目标偏移。</para></param>
    /// <param name="count">Maximum byte count to read. <para>最多读取的字节数。</para></param>
    /// <returns>
    /// The number of bytes actually read; 0 at the end of the stream.
    /// <para>实际读取的字节数；位于流末尾时为 0。</para>
    /// </returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateReadArgs(buffer, offset, count);
        var total = 0;
        while (total < count && _position < Length)
        {
            var n = ReadCurrentBlock(buffer.AsSpan(offset + total, count - total));
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>
    /// Reads a sequence of bytes into the destination span.
    /// <para>将一段字节读入目标 span。</para>
    /// </summary>
    /// <param name="buffer">Destination span. <para>目标 span。</para></param>
    /// <returns>
    /// The number of bytes actually read; 0 at the end of the stream.
    /// <para>实际读取的字节数；位于流末尾时为 0。</para>
    /// </returns>
    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && _position < Length)
        {
            var n = ReadCurrentBlock(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>
    /// Asynchronously reads a sequence of bytes from the current position.
    /// <para>从当前位置异步读取一段字节。</para>
    /// </summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">Destination offset. <para>目标偏移。</para></param>
    /// <param name="count">Maximum byte count to read. <para>最多读取的字节数。</para></param>
    /// <param name="cancellationToken">
    /// Cancellation token carried through the underlying file reads.
    /// <para>取消令牌，贯穿底层文件读取。</para>
    /// </param>
    /// <returns>
    /// The number of bytes actually read; 0 at the end of the stream.
    /// <para>实际读取的字节数；位于流末尾时为 0。</para>
    /// </returns>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateReadArgs(buffer, offset, count);
        var total = 0;
        while (total < count && _position < Length)
        {
            var n = await ReadCurrentBlockAsync(buffer.AsMemory(offset + total, count - total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>
    /// Reads one byte at the current position, or -1 at the end of the stream.
    /// <para>读取当前位置的一个字节；流末尾返回 -1。</para>
    /// </summary>
    /// <returns>
    /// The byte value (0..255), or -1 at the end of the stream.
    /// <para>读取的字节值（0..255），流末尾为 -1。</para>
    /// </returns>
    public override int ReadByte()
    {
        Span<byte> single = stackalloc byte[1];
        var n = Read(single);
        return n == 0 ? -1 : single[0];
    }

    /// <summary>
    /// Seeks to a new position relative to the given origin.
    /// <para>以给定原点为基准定位到新位置。</para>
    /// </summary>
    /// <param name="offset">Byte offset relative to <paramref name="origin"/>. <para>相对 <paramref name="origin"/> 的字节偏移。</para></param>
    /// <param name="origin">Seek origin. <para>定位原点。</para></param>
    /// <returns>
    /// The new position.
    /// <para>新的位置。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The computed position is negative or beyond the stream length.
    /// <para>计算出的位置为负数或超出流长度。</para>
    /// </exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
        return _position;
    }

    private void ValidateReadArgs(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count are outside the buffer bounds.");
        }
    }

    private int ReadCurrentBlock(Span<byte> destination)
    {
        var blockIndex = (int)(_position / _blockSize);
        var offsetInBlock = (int)(_position % _blockSize);
        var available = (int)Math.Min((long)_blockSize - offsetInBlock, Length - _position);
        available = Math.Min(available, destination.Length);
        if (blockIndex >= _blockOffsets.Length || available <= 0)
        {
            return 0;
        }

        _baseStream.Seek(_blockOffsets[blockIndex] + offsetInBlock, SeekOrigin.Begin);
        Span<byte> buffer = available <= 512 ? stackalloc byte[available] : new byte[available];
        var read = 0;
        while (read < available)
        {
            var n = _baseStream.Read(buffer[read..]);
            if (n <= 0)
            {
                throw new CompoundFileCorruptedException($"The container ends {available - read} bytes short while reading block {blockIndex} of a compound file stream.");
            }

            read += n;
        }

        buffer.CopyTo(destination);
        _position += available;
        return available;
    }

    private async ValueTask<int> ReadCurrentBlockAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var blockIndex = (int)(_position / _blockSize);
        var offsetInBlock = (int)(_position % _blockSize);
        var available = (int)Math.Min((long)_blockSize - offsetInBlock, Length - _position);
        available = Math.Min(available, destination.Length);
        if (blockIndex >= _blockOffsets.Length || available <= 0)
        {
            return 0;
        }

        _baseStream.Seek(_blockOffsets[blockIndex] + offsetInBlock, SeekOrigin.Begin);
        var buffer = destination[..available];
        var read = 0;
        while (read < available)
        {
            var n = await _baseStream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new CompoundFileCorruptedException($"The container ends {available - read} bytes short while reading block {blockIndex} of a compound file stream.");
            }

            read += n;
        }

        _position += available;
        return available;
    }
}