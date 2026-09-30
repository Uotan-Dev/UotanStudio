using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.AndroidMeta.Art;

/// <summary>
/// Describes a bounded DEX entry in a VDEX 006 or 010 container.
/// <para>描述 VDEX 006 或 010 容器中经过边界检查的 DEX 条目。</para>
/// </summary>
/// <param name="Offset">The container-relative offset. 容器内偏移。</param>
/// <param name="Size">The DEX byte length. DEX 字节长度。</param>
/// <param name="LocationChecksum">The original location checksum. 原始位置校验和。</param>
public sealed record VdexEntry(long Offset, uint Size, uint LocationChecksum)
{
    internal bool Compact { get; init; }
    internal long SharedOffset { get; init; }
    internal uint SharedSize { get; init; }
    internal uint QuickTable { get; init; }
}

/// <summary>
/// Reads Android 8.0/8.1 VDEX 006/010 containers and restores their standard DEX instructions.
/// <para>读取 Android 8.0/8.1 VDEX 006/010 容器并恢复标准 DEX 指令。</para>
/// </summary>
/// <remarks>
/// Shared code items, CompactDex and vendor layouts are rejected. DEX hashes are checked, but ART semantic verification is not performed.
/// <para>拒绝共享代码项、CompactDex 和厂商布局。检查 DEX 哈希，但不执行 ART 语义验证。</para>
/// </remarks>
internal static partial class VdexFile
{
    /// <summary>
    /// Inspects the complete section layout without allocating DEX images.
    /// <para>检查完整分区布局，不将 DEX 镜像载入内存。</para>
    /// </summary>
    /// <param name="path">The VDEX input path. VDEX 输入路径。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <returns>The ordered DEX entries. 按顺序排列的 DEX 条目。</returns>
    public static IReadOnlyList<VdexEntry> Inspect(string path, CancellationToken cancellationToken = default)
    {
        using var input = File.OpenRead(path);
        return Parse(input, cancellationToken).Entries.AsReadOnly();
    }

    /// <summary>
    /// Atomically publishes dequickened DEX files into a new directory after original hash verification.
    /// <para>去除 quickening 并验证原始哈希后，将 DEX 文件原子发布到新目录。</para>
    /// </summary>
    /// <param name="path">The VDEX input path. VDEX 输入路径。</param>
    /// <param name="outputDirectory">A directory that must not exist. 必须尚不存在的输出目录。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <returns>A task completing after all entries are published. 全部条目发布后完成的任务。</returns>
    public static async Task ExtractAsync(string path, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("The extraction destination must not exist.");
        }
        using var input = File.OpenRead(path);
        var layout = Parse(input, cancellationToken);
        if (layout.Entries.Count == 0)
        {
            throw new NotSupportedException("This VDEX contains no exportable DEX section.");
        }
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        Directory.CreateDirectory(temporary);
        try
        {
            var quick = new ArtData(input, layout.QuickOffset, layout.QuickSize, cancellationToken);
            long quickCursor = 0;
            for (var i = 0; i < layout.Entries.Count; i++)
            {
                var entry = layout.Entries[i];
                var name = i == 0 ? "classes.dex" : $"classes{i + 1}.dex";
                using var output = new FileStream(Path.Combine(temporary, name), FileMode.CreateNew, FileAccess.ReadWrite);
                if (entry.Compact)
                {
                    await ConvertCompactAsync(input, entry, output, quick, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                input.Position = entry.Offset;
                await CopyAsync(input, output, entry.Size, cancellationToken).ConfigureAwait(false);
                var dex = new ArtData(output, 0, entry.Size, cancellationToken);
                if (layout.Version is 19 or 21)
                {
                    var table = quick.Length == 0 ? null : new CompactOffsetTable(quick, entry.QuickTable, dex.U32(88));
                    Restore(dex, quick, ref quickCursor, true, cancellationToken, modernTable: table, modern: true);
                }
                else if (layout.Version == 10)
                {
                    var records = Read010Records(quick, i, layout.Entries.Count);
                    Restore(dex, quick, ref quickCursor, true, cancellationToken, records);
                }
                else
                {
                    Restore(dex, quick, ref quickCursor, layout.QuickSize != 0, cancellationToken);
                }
                dex.VerifyDexHashes();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (layout.Version == 6 && quickCursor != layout.QuickSize)
            {
                throw new InvalidDataException("Unused VDEX quickening records.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, true);
            }
        }
    }

    /// <summary>
    /// Replaces all DEX entries with validated standard DEX files and discards stale ART caches.
    /// <para>使用经过验证的标准 DEX 替换所有条目并清除失效的 ART 缓存。</para>
    /// </summary>
    /// <remarks>
    /// The result requires runtime verification and OAT recompilation; existing compiled code must not be reused.
    /// <para>结果需要运行时重新验证和重新编译 OAT，不得复用原有机器码。</para>
    /// </remarks>
    /// <param name="templatePath">The VDEX 006 or 010 template. VDEX 006 或 010 模板路径。</param>
    /// <param name="dexPaths">Replacement DEX paths in original order. 按原始顺序排列的替换 DEX 路径。</param>
    /// <param name="outputPath">The atomically replaced output path. 原子替换的输出路径。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <returns>A task completing after publication. 发布后完成的任务。</returns>
    public static async Task RebuildAsync(string templatePath, IReadOnlyList<string> dexPaths, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dexPaths);
        using var template = File.OpenRead(templatePath);
        var layout = Parse(template, cancellationToken);
        if (layout.Entries.Count == 0)
        {
            throw new NotSupportedException("This VDEX contains no exportable DEX section.");
        }
        if (layout.Version is 19 or 21)
        {
            throw new NotSupportedException("VDEX 019/021 rebuilding is not supported; export standard DEX for recompilation.");
        }
        if (dexPaths.Count != layout.Entries.Count)
        {
            throw new InvalidDataException("Replacement count differs from the template.");
        }
        var destination = Path.GetFullPath(outputPath);
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var headerSize = 24 + 4 * dexPaths.Count;
                output.SetLength(headerSize);
                output.Write(layout.Version == 6 ? "vdex006\0"u8 : "vdex010\0"u8);
                long total = 0;
                for (var i = 0; i < dexPaths.Count; i++)
                {
                    using var source = File.OpenRead(dexPaths[i]);
                    if (source.Length > uint.MaxValue || total + source.Length > uint.MaxValue)
                    {
                        throw new InvalidDataException("VDEX DEX section exceeds UInt32.");
                    }
                    var start = headerSize + total;
                    output.Position = start;
                    await CopyAsync(source, output, source.Length, cancellationToken).ConfigureAwait(false);
                    var dex = new ArtData(output, start, source.Length, cancellationToken);
                    dex.ValidateDex();
                    long cursor = 0;
                    Restore(dex, new ArtData(output, 0, 0, cancellationToken), ref cursor, false, cancellationToken);
                    dex.VerifyDexHashes();
                    new ArtData(output, 0, output.Length, cancellationToken).Write32(24 + i * 4, dex.U32(8));
                    total += source.Length;
                }
                var header = new ArtData(output, 0, output.Length, cancellationToken);
                header.Write32(8, (uint)dexPaths.Count);
                header.Write32(12, (uint)total);
                if (layout.Version == 10)
                {
                    header.Write32(20, (uint)(dexPaths.Count * 4));
                    output.SetLength(output.Length + dexPaths.Count * 4);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            _ = Inspect(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static (List<VdexEntry> Entries, long QuickOffset, uint QuickSize, int Version) Parse(Stream input, CancellationToken token)
    {
        var data = new ArtData(input, 0, input.Length, token);
        Span<byte> magic = stackalloc byte[8];
        data.Read(0, magic);
        if (!magic[..4].SequenceEqual("vdex"u8))
        {
            throw new InvalidDataException("Not a VDEX container.");
        }
        if (magic[4..].SequenceEqual("019\0"u8) || magic[4..].SequenceEqual("021\0"u8))
        {
            return ParseModern(input, magic[5] == (byte)'1' ? 19 : 21, token);
        }
        var version = magic[4..].SequenceEqual("006\0"u8) ? 6 : magic[4..].SequenceEqual("010\0"u8) ? 10 : 0;
        if (version == 0)
        {
            throw new NotSupportedException("Only VDEX 006 and 010 are supported; other versions require different layouts.");
        }
        var count = data.U32(8);
        if (count is 0 or > 4096)
        {
            throw new InvalidDataException("VDEX DEX count is outside the supported resource limit.");
        }
        long start = 24 + 4L * count;
        var dexEnd = start + data.U32(12);
        var quickStart = dexEnd + data.U32(16);
        var quickSize = data.U32(20);
        if (quickStart + quickSize != input.Length)
        {
            throw new InvalidDataException("VDEX section lengths do not match its file length.");
        }
        var entries = new List<VdexEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            data.Check(start, 112);
            var size = data.U32(start + 32);
            if (size < 112 || size > dexEnd - start)
            {
                throw new InvalidDataException("DEX extends beyond its VDEX section.");
            }
            new ArtData(input, start, size, token).ValidateDex();
            entries.Add(new VdexEntry(start, size, data.U32(24 + 4L * i)));
            start += size;
        }
        if (start != dexEnd)
        {
            throw new InvalidDataException("VDEX DEX count and section size disagree.");
        }
        if (version == 10 && quickSize != 0)
        {
            var quick = new ArtData(input, quickStart, quickSize, token);
            var indexStart = quickSize - 4L * count;
            quick.Check(indexStart, 4L * count);
            var previous = quick.U32(indexStart);
            for (var i = 1; i <= count; i++)
            {
                var next = i == count ? indexStart : quick.U32(indexStart + 4L * i);
                if (next < previous || next > indexStart || (next - previous) % 8 != 0)
                {
                    throw new InvalidDataException("Invalid VDEX 010 quickening index tables.");
                }
                previous = (uint)next;
            }
        }
        return (entries, quickStart, quickSize, version);
    }

    private static Dictionary<uint, (long Start, long End)> Read010Records(ArtData quick, int index, int count)
    {
        var result = new Dictionary<uint, (long, long)>();
        if (quick.Length == 0)
        {
            return result;
        }
        var indexStart = quick.Length - 4L * count;
        var dataEnd = quick.U32(indexStart);
        long start = quick.U32(indexStart + 4L * index);
        var end = index + 1 == count ? indexStart : quick.U32(indexStart + 4L * (index + 1));
        if ((end - start) / 8 > 1_000_000)
        {
            throw new InvalidDataException("VDEX 010 method count exceeds the resource limit.");
        }
        for (; start < end; start += 8)
        {
            var code = quick.U32(start);
            long at = quick.U32(start + 4);
            if (at > dataEnd - 4L)
            {
                throw new InvalidDataException("VDEX 010 quickening blob overlaps its tables.");
            }
            var size = quick.U32(at);
            if ((size & 1) != 0 || at + 4 + size > dataEnd || !result.TryAdd(code, (at + 4, at + 4 + size)))
            {
                throw new InvalidDataException("Invalid or duplicate VDEX 010 quickening blob.");
            }
        }
        return result;
    }

    private static async Task CopyAsync(Stream input, Stream output, long size, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (size > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(size, buffer.Length)), token).ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("Truncated ART input.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            size -= count;
        }
        token.ThrowIfCancellationRequested();
    }
}