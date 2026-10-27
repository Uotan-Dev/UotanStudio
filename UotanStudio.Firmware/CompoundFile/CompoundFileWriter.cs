using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.CompoundFile;

/// <summary>
/// A single stream to store inside a compound file (OLE2/CFB) container built by
/// <see cref="CompoundFileWriter.BuildAsync"/>.
/// <para>由 <see cref="CompoundFileWriter.BuildAsync"/> 构建的复合文档（OLE2/CFB）容器中的单个流。</para>
/// </summary>
/// <param name="Path">Slash-separated path from the root, e.g. <c>NV_NUMBERED_ITEMS/NV_ITEM_ARRAY</c>.
/// <para>从根开始的斜杠分隔路径，例如 <c>NV_NUMBERED_ITEMS/NV_ITEM_ARRAY</c>。</para></param>
/// <param name="Data">The stream content; empty streams are stored as zero-length entries.
/// <para>流内容；空流以零长度条目存储。</para></param>
public sealed record CompoundFileStreamWriterEntry(string Path, ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// Maximum accepted stream size for a built container (64 MiB), matching
    /// <see cref="CompoundFileReader"/> limits.
    /// <para>构建容器接受的单流大小上限（64 MiB），与 <see cref="CompoundFileReader"/> 的上限一致。</para>
    /// </summary>
    public const long MaxStreamSize = 64L * 1024 * 1024;
}

/// <summary>
/// Bounded writer producing version-3 (512-byte sector) compound file (OLE2/CFB) containers that
/// <see cref="CompoundFileReader"/> parses back. Streams at or above the 4096-byte mini-stream cutoff
/// are stored in regular sectors; smaller streams live in the mini stream.
/// <para>有界的版本 3（512 字节扇区）复合文档（OLE2/CFB）容器写入器，产物可被
/// <see cref="CompoundFileReader"/> 解析。达到 4096 字节 mini 流阈值的流存入常规扇区；更小的流
/// 存放在 mini 流中。</para>
/// </summary>
public static class CompoundFileWriter
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFD;
    private const uint NoStream = 0xFFFFFFFF;

    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const int HeaderLength = 512;
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const uint MiniStreamCutoff = 0x1000;
    private const int DirectoryEntryLength = 128;
    private const int MaxTotalSectors = 1 << 20;
    private const int MaxEntries = 1 << 16;
    private const int MaxFatSectors = (1 << 20) / (SectorSize / 4);

    /// <summary>
    /// Builds a compound file container holding the given streams and writes it to
    /// <paramref name="path"/> atomically: the bytes are staged in an adjacent temporary file and
    /// moved over the destination only after the whole container has been written and re-parsed.
    /// <para>构建承载给定流的复合文档容器并原子写入 <paramref name="path"/>：字节先写入同目录
    /// 临时文件，整个容器写完并重新解析后才移动到目标。</para></summary>
    /// <param name="path">Destination file path; missing parents are created.
    /// <para>目标文件路径；父目录缺失时自动创建。</para></param>
    /// <param name="streams">Streams to store. Paths must be unique, slash-separated and carry
    /// 1-31 character names per component. <para>待存储的流。路径必须唯一、以斜杠分隔且每个
    /// 组件名长度为 1-31 字符。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">Two streams share a path (case-insensitive) or a name
    /// component is invalid. <para>两个流路径重复（不区分大小写）或名称组件非法。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A stream exceeds the accepted size or the
    /// container exceeds the sector budget. <para>流超过大小上限或容器超出扇区预算。</para></exception>
    public static async Task BuildAsync(string path, IReadOnlyList<CompoundFileStreamWriterEntry> streams,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
        {
            throw new ArgumentException("At least one stream is required.", nameof(streams));
        }

        if (streams.Count >= MaxEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(streams),
                $"A built container accepts at most {MaxEntries - 1} streams.");
        }

        var byPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in streams)
        {
            ValidateStreamPath(stream.Path);
            if (!byPath.Add(stream.Path))
            {
                throw new ArgumentException($"Two streams share the path '{stream.Path}'.", nameof(stream));
            }

            if (stream.Data.Length > CompoundFileStreamWriterEntry.MaxStreamSize)
            {
                throw new ArgumentOutOfRangeException(nameof(streams),
                    $"Stream '{stream.Path}' is {stream.Data.Length} bytes; at most " +
                    CompoundFileStreamWriterEntry.MaxStreamSize + " bytes are accepted.");
            }
        }

        var staged = TempPathHelper.CreateAdjacentPath(path);
        try
        {
            await BuildCoreAsync(staged, streams, cancellationToken).ConfigureAwait(false);
            await VerifyBuiltAsync(staged, streams, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            TempPathHelper.SafeDelete(staged);
        }
    }

    private static void ValidateStreamPath(string streamPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamPath);
        var components = streamPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Length != streamPath.Split('/').Count(c => c.Length > 0))
        {
            throw new ArgumentException($"The stream path '{streamPath}' is not a normalized slash-separated path.",
                nameof(streamPath));
        }

        foreach (var component in components)
        {
            if (component.Length is < 1 or > 31 || component is "." or "..")
            {
                throw new ArgumentException(
                    $"The stream path '{streamPath}' carries the invalid name component '{component}'.",
                    nameof(streamPath));
            }
        }
    }

    private static async Task VerifyBuiltAsync(string stagedPath,
        IReadOnlyList<CompoundFileStreamWriterEntry> streams, CancellationToken cancellationToken)
    {
        using var verify = await CompoundFileReader.OpenAsync(stagedPath, cancellationToken).ConfigureAwait(false);
        foreach (var stream in streams)
        {
            var entry = verify.TryGetEntryByPath(stream.Path);
            if (entry is null || entry.Type != CompoundFileEntryType.Stream || entry.StreamSize != stream.Data.Length)
            {
                throw new CompoundFileException($"Post-build verification failed for stream '{stream.Path}'.");
            }

            await using var opened = verify.OpenStream(entry);
            var buffer = new byte[stream.Data.Length];
            await ReadExactAsync(opened, buffer, cancellationToken).ConfigureAwait(false);
            if (!buffer.AsSpan().SequenceEqual(stream.Data.Span))
            {
                throw new CompoundFileException(
                    $"Post-build verification mismatched the content of stream '{stream.Path}'.");
            }
        }
    }

    private static async Task BuildCoreAsync(string stagedPath,
        IReadOnlyList<CompoundFileStreamWriterEntry> streams, CancellationToken cancellationToken)
    {
        var regular = new List<CompoundFileStreamWriterEntry>();
        var mini = new List<CompoundFileStreamWriterEntry>();
        foreach (var stream in streams)
        {
            if (stream.Data.Length >= MiniStreamCutoff)
            {
                regular.Add(stream);
            }
            else
            {
                mini.Add(stream);
            }
        }

        var miniSectorCount = 0;
        var miniChainStarts = new Dictionary<CompoundFileStreamWriterEntry, uint>();
        foreach (var stream in mini)
        {
            miniChainStarts[stream] = (uint)miniSectorCount;
            miniSectorCount += Math.Max(1, (stream.Data.Length + MiniSectorSize - 1) / MiniSectorSize);
        }

        var miniFat = new List<uint>();
        foreach (var stream in mini)
        {
            var blocks = Math.Max(1, (stream.Data.Length + MiniSectorSize - 1) / MiniSectorSize);
            var start = (int)miniChainStarts[stream];
            for (var b = 0; b < blocks; b++)
            {
                miniFat.Add(b + 1 < blocks ? (uint)(start + b + 1) : EndOfChain);
            }
        }

        var miniStreamSize = miniSectorCount * MiniSectorSize;
        var miniStreamRegularBlocks = (int)Math.Ceiling(miniStreamSize / (double)SectorSize);

        var storages = new List<string>();
        foreach (var stream in streams)
        {
            var components = stream.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < components.Length - 1; i++)
            {
                var storage = string.Join('/', components[..(i + 1)]);
                if (!storages.Contains(storage, StringComparer.OrdinalIgnoreCase))
                {
                    storages.Add(storage);
                }
            }
        }

        var entryCount = 1 + storages.Count + streams.Count;
        var directorySectors = (int)Math.Ceiling(entryCount * (double)DirectoryEntryLength / SectorSize);
        var dataSectors = regular.Sum(stream => (long)Math.Ceiling(stream.Data.Length / (double)SectorSize))
            + miniStreamRegularBlocks;

        var fatSectors = 1;
        int totalSectors;
        int miniFatSectors;
        int fatStart;
        int miniFatStart;
        while (true)
        {
            miniFatSectors = miniFat.Count == 0
                ? 0
                : (int)Math.Ceiling(miniFat.Count * 4 / (double)SectorSize);
            totalSectors = (int)(dataSectors + directorySectors + fatSectors + miniFatSectors);
            var requiredFatSectors = (int)Math.Ceiling(totalSectors * 4 / (double)SectorSize);
            if (requiredFatSectors <= fatSectors)
            {
                fatStart = (int)(dataSectors + directorySectors);
                miniFatStart = fatStart + fatSectors;
                break;
            }

            fatSectors = requiredFatSectors;
        }

        if (totalSectors > MaxTotalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(streams),
                $"The container would need {totalSectors} sectors; at most {MaxTotalSectors} are accepted.");
        }

        if (fatSectors > MaxFatSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(streams),
                $"The container would need {fatSectors} FAT sectors; at most {MaxFatSectors} are accepted.");
        }

        var fat = new uint[fatSectors * (SectorSize / 4)];
        Array.Fill(fat, FreeSect);

        var streamChainStarts = new Dictionary<CompoundFileStreamWriterEntry, uint>();
        var nextSector = 0;
        foreach (var stream in regular)
        {
            var blocks = Math.Max(1, (stream.Data.Length + SectorSize - 1) / SectorSize);
            streamChainStarts[stream] = (uint)nextSector;
            for (var b = 0; b < blocks; b++)
            {
                fat[nextSector + b] = b + 1 < blocks ? (uint)(nextSector + b + 1) : EndOfChain;
            }

            nextSector += blocks;
        }

        var miniStreamStart = NoStream;
        if (miniStreamRegularBlocks > 0)
        {
            miniStreamStart = (uint)nextSector;
            for (var b = 0; b < miniStreamRegularBlocks; b++)
            {
                fat[nextSector + b] =
                    b + 1 < miniStreamRegularBlocks ? (uint)(nextSector + b + 1) : EndOfChain;
            }

            nextSector += miniStreamRegularBlocks;
        }

        var directoryStart = nextSector;
        for (var b = 0; b < directorySectors; b++)
        {
            fat[nextSector + b] = b + 1 < directorySectors ? (uint)(nextSector + b + 1) : EndOfChain;
        }

        nextSector += directorySectors;
        fatStart = nextSector;
        for (var b = 0; b < fatSectors; b++)
        {
            fat[nextSector + b] = b + 1 < fatSectors ? (uint)(nextSector + b + 1) : EndOfChain;
        }

        nextSector += fatSectors;
        miniFatStart = nextSector;
        for (var b = 0; b < miniFatSectors; b++)
        {
            fat[nextSector + b] = b + 1 < miniFatSectors ? (uint)(nextSector + b + 1) : EndOfChain;
        }

        nextSector += miniFatSectors;
        if (nextSector != totalSectors)
        {
            throw new InvalidOperationException("The container layout accounting drifted.");
        }

        var entries = new List<DirectoryEntry> { new("Root Entry", 5, miniStreamStart, miniStreamSize) };
        foreach (var storage in storages)
        {
            var name = storage.Split('/')[^1];
            entries.Add(new DirectoryEntry(name, 1, NoStream, 0)
            {
                ParentPath = storage.Contains('/') ? storage[..storage.LastIndexOf('/')] : string.Empty,
            });
        }

        foreach (var stream in streams)
        {
            var isMini = stream.Data.Length > 0 && stream.Data.Length < MiniStreamCutoff;
            var start = isMini
                ? miniChainStarts[stream]
                : streamChainStarts.GetValueOrDefault(stream, NoStream);
            var components = stream.Path.Split('/');
            entries.Add(new DirectoryEntry(components[^1], 2, start, stream.Data.Length)
            {
                ParentPath = components.Length > 1 ? string.Join('/', components[..^1]) : string.Empty,
            });
        }

        LinkSiblings(entries);

        await using var output = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteHeaderAsync(output, fatStart, fatSectors, directoryStart, miniFatStart, miniFatSectors,
            cancellationToken).ConfigureAwait(false);

        var dataBuffer = new byte[SectorSize];
        foreach (var stream in regular)
        {
            var remaining = stream.Data.Length;
            var offset = 0;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, SectorSize);
                Array.Clear(dataBuffer);
                stream.Data.Span.Slice(offset, chunk).CopyTo(dataBuffer);
                await output.WriteAsync(dataBuffer, cancellationToken).ConfigureAwait(false);
                offset += chunk;
                remaining -= chunk;
            }
        }

        if (miniStreamRegularBlocks > 0)
        {
            var container = new byte[miniStreamRegularBlocks * SectorSize];
            var cursor = 0;
            foreach (var stream in mini)
            {
                var remaining = stream.Data.Length;
                var offset = 0;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, MiniSectorSize);
                    stream.Data.Span.Slice(offset, chunk).CopyTo(container.AsSpan(cursor * MiniSectorSize));
                    cursor++;
                    offset += chunk;
                    remaining -= chunk;
                }

                if (stream.Data.Length == 0)
                {
                    cursor++;
                }
            }

            for (var b = 0; b < miniStreamRegularBlocks; b++)
            {
                await output.WriteAsync(container.AsMemory(b * SectorSize, SectorSize), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var directoryBytes = new byte[directorySectors * SectorSize];
        for (var i = 0; i < entries.Count; i++)
        {
            EncodeDirectoryEntry(entries[i], directoryBytes.AsSpan(i * DirectoryEntryLength, DirectoryEntryLength));
        }

        for (var b = 0; b < directorySectors; b++)
        {
            await output.WriteAsync(directoryBytes.AsMemory(b * SectorSize, SectorSize), cancellationToken)
                .ConfigureAwait(false);
        }

        var fatBytes = new byte[fatSectors * SectorSize];
        for (var i = 0; i < fat.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(4 * i), fat[i]);
        }

        for (var b = 0; b < fatSectors; b++)
        {
            await output.WriteAsync(fatBytes.AsMemory(b * SectorSize, SectorSize), cancellationToken)
                .ConfigureAwait(false);
        }

        if (miniFatSectors > 0)
        {
            var miniFatBytes = new byte[miniFatSectors * SectorSize];
            for (var i = 0; i < miniFat.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(4 * i), miniFat[i]);
            }

            for (var b = 0; b < miniFatSectors; b++)
            {
                await output.WriteAsync(miniFatBytes.AsMemory(b * SectorSize, SectorSize), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void LinkSiblings(List<DirectoryEntry> entries)
    {
        var byParent = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < entries.Count; i++)
        {
            if (!byParent.TryGetValue(entries[i].ParentPath, out var list))
            {
                list = [];
                byParent[entries[i].ParentPath] = list;
            }

            list.Add(i);
        }

        foreach (var (parentPath, list) in byParent)
        {
            for (var i = 0; i < list.Count; i++)
            {
                entries[list[i]].RightSibling = i + 1 < list.Count ? (uint)list[i + 1] : NoStream;
            }

            var parentIndex = FindParentIndex(entries, parentPath);
            if (parentIndex > 0 || (parentIndex == 0 && parentPath.Length == 0))
            {
                entries[parentIndex].Child = (uint)list[0];
            }
        }
    }

    private static int FindParentIndex(List<DirectoryEntry> entries, string parentPath)
    {
        if (parentPath.Length == 0)
        {
            return 0;
        }

        var lastSlash = parentPath.LastIndexOf('/');
        var name = lastSlash >= 0 ? parentPath[(lastSlash + 1)..] : parentPath;
        var grandParent = lastSlash >= 0 ? parentPath[..lastSlash] : string.Empty;
        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].Kind == 1 && entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && entries[i].ParentPath == grandParent)
            {
                return i;
            }
        }

        return -1;
    }

    private static void EncodeDirectoryEntry(DirectoryEntry entry, Span<byte> destination)
    {
        destination.Clear();
        Encoding.Unicode.GetBytes(entry.Name).CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(64, 2),
            (ushort)(Encoding.Unicode.GetByteCount(entry.Name) + 2));
        destination[66] = (byte)entry.Kind;
        destination[67] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(68, 4), entry.LeftSibling);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(72, 4), entry.RightSibling);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(76, 4), entry.Child);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(116, 4), entry.StartSector);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(120, 4), (uint)entry.StreamSize);
    }

    private static async Task WriteHeaderAsync(FileStream output, int fatStart, int fatSectors, int directoryStart,
        int miniFatStart, int miniFatSectors, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        Signature.CopyTo(header);
        Array.Fill<byte>(header, 0xFF, 0, HeaderLength);
        Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), (uint)directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), MiniStreamCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60),
            miniFatSectors > 0 ? (uint)miniFatStart : NoStream);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), EndOfChain);
        for (var i = 0; i < fatSectors && i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + (4 * i)), (uint)(fatStart + i));
        }

        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException($"The container ended after {read} of {buffer.Length} expected bytes.");
            }

            read += n;
        }
    }

    private sealed class DirectoryEntry(string name, int kind, uint startSector, long streamSize)
    {
        public string Name { get; } = name;
        public int Kind { get; } = kind;
        public uint StartSector { get; set; } = startSector;
        public long StreamSize { get; set; } = streamSize;
        public uint LeftSibling { get; set; } = NoStream;
        public uint RightSibling { get; set; } = NoStream;
        public uint Child { get; set; } = NoStream;
        public string ParentPath { get; init; } = string.Empty;
    }
}

/// <summary>
/// Adjacent temporary file helper for atomic container publication.
/// <para>用于原子发布容器的同目录临时文件辅助。</para>
/// </summary>
internal static class TempPathHelper
{
    /// <summary>Creates an adjacent temporary path for the given target.
    /// <para>为目标创建同目录临时路径。</para></summary>
    public static string CreateAdjacentPath(string target)
    {
        var directory = Path.GetDirectoryName(target);
        var name = Path.GetFileName(target);
        return Path.Combine(directory ?? ".", $".{name}.tmp-{Guid.NewGuid():N}");
    }

    /// <summary>Deletes the temporary file if it exists, ignoring failures.
    /// <para>删除存在的临时文件，忽略失败。</para></summary>
    public static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}