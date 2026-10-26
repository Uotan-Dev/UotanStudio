using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// Reads and rewrites GPT partition metadata in a caller-owned whole-image byte array while preserving
/// every byte outside the headers and entry arrays.
/// <para>在调用方持有的整镜像字节数组中读取并回写 GPT 分区元数据，保留头部与分区表项数组之外的每一个字节。</para>
/// </summary>
public static class GptImageEditor
{
    /// <summary>Maximum partition-entry count accepted by the image editor. <para>镜像编辑器接受的分区表项数量上限。</para></summary>
    private const int MaximumEntryCount = 1024;

    /// <summary>Maximum byte distance searched around a header for its entry array. <para>在头部附近搜索条目数组的最大字节距离。</para></summary>
    private const long MaximumMetadataDistance = 1024 * 1024;

    /// <summary>
    /// Checks whether the given bytes carry the GPT signature (<c>EFI PART</c>) at a sector-aligned offset.
    /// <para>检查给定字节是否在扇区对齐偏移上携带 GPT 签名（<c>EFI PART</c>）。</para>
    /// </summary>
    /// <param name="data">The bytes to inspect. <para>待检查的字节。</para></param>
    /// <returns><see langword="true"/> when a sector-aligned signature is present. <para>存在扇区对齐签名时为 <see langword="true"/>。</para></returns>
    public static bool HasSignature(ReadOnlySpan<byte> data) => FindHeaderOffset(data) >= 0;

    /// <summary>
    /// Returns the first sector-aligned <c>EFI PART</c> signature offset, or <c>-1</c> when there is none.
    /// <para>返回首个扇区对齐的 <c>EFI PART</c> 签名偏移，未找到时返回 <c>-1</c>。</para>
    /// </summary>
    /// <param name="data">The bytes to scan. <para>待扫描的字节。</para></param>
    /// <returns>The byte offset of the first aligned signature, or <c>-1</c>. <para>首个对齐签名的字节偏移，未找到时为 <c>-1</c>。</para></returns>
    public static long FindHeaderOffset(ReadOnlySpan<byte> data)
    {
        for (var search = 0; search < data.Length;)
        {
            var relative = data[search..].IndexOf(GptLayout.Signature);
            if (relative < 0)
            {
                break;
            }

            var offset = search + relative;
            if (offset % GptLayout.SectorSize == 0)
            {
                return offset;
            }

            search = offset + 1;
        }

        return -1;
    }

    /// <summary>
    /// Parses a GPT image: locates every sector-aligned header, validates the header CRC, resolves each
    /// entry array, and takes the copy carrying the most partitions as the partition-table source.
    /// <para>解析 GPT 镜像：定位每个扇区对齐的头部，校验头 CRC，解析每个条目数组，并以分区最多的副本作为分区表来源。</para>
    /// </summary>
    /// <param name="image">The image bytes to parse. <para>待解析的镜像字节。</para></param>
    /// <param name="table">The parsed table on success; <see langword="null"/> on failure. Its fingerprint hashes each discovered header block followed by the selected entry array. <para>成功时的解析结果；失败时为 <see langword="null"/>。其指纹对每个已发现的头块与选中的条目数组依次求哈希。</para></param>
    /// <returns><see langword="true"/> when at least one CRC-verified header and entry array were found. <para>找到至少一个 CRC 校验通过的头部与条目数组时为 <see langword="true"/>。</para></returns>
    public static bool TryRead(byte[] image, out GptTable table) => TryRead(image, null, out table, out _);

    /// <summary>
    /// Parses one selected GPT table copy: <see langword="null"/> <paramref name="headerLba"/> takes the copy
    /// holding the most partitions, while a logical block address selects that exact copy so a container with
    /// several tables can switch between them.
    /// <para>解析选定的单份 GPT 表副本：<paramref name="headerLba"/> 为 <see langword="null"/> 时取分区最多的副本，
    /// 给出逻辑块地址时精确选择该副本，便于在打包了多张表的容器间切换。</para>
    /// </summary>
    /// <param name="image">The image bytes to parse. <para>待解析的镜像字节。</para></param>
    /// <param name="headerLba">Header block to read, or <see langword="null"/> for the richest copy. <para>要读取的头部块；<see langword="null"/> 表示分区最多的副本。</para></param>
    /// <param name="table">The parsed table on success; <see langword="null"/> on failure. <para>成功时的解析结果；失败时为 <see langword="null"/>。</para></param>
    /// <param name="source">The copy the table was read from, as reported by <see cref="DescribeCopies"/>; meaningful only on success. <para>表来自的副本，与 <see cref="DescribeCopies"/> 的条目一致；仅在成功时有意义。</para></param>
    /// <returns><see langword="true"/> when the requested copy validated; <see langword="false"/> for an unknown block or an invalid image. <para>请求的副本通过校验时为 <see langword="true"/>；未知块或无效镜像为 <see langword="false"/>。</para></returns>
    public static bool TryRead(byte[] image, ulong? headerLba, out GptTable table, out GptTableCopy source)
    {
        ArgumentNullException.ThrowIfNull(image);
        table = null!;
        source = default;
        if (!TryLocate(image, headerLba, out var copies, out var selected, out var partitions, out var diskSizeBytes, out var diskGuid))
        {
            return false;
        }

        var header = image.AsSpan((int)selected.HeaderOffset, GptLayout.HeaderSize);
        source = new GptTableCopy((ulong)(selected.HeaderOffset / GptLayout.SectorSize), selected.EntryCount, partitions.Count);
        table = new GptTable(
            diskGuid,
            GptLayout.SectorSize,
            diskSizeBytes / (ulong)GptLayout.SectorSize,
            partitions,
            ComputeFingerprint(image, copies, selected),
            (uint)selected.HeaderSize,
            BinaryPrimitives.ReadUInt64LittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[48..]),
            selected.EntryCount,
            (uint)GptLayout.EntrySize,
            (ulong)(selected.EntriesOffset / GptLayout.SectorSize),
            (ulong)(copies[^1].EntriesOffset / GptLayout.SectorSize));
        return true;
    }

    /// <summary>
    /// Lists every CRC-verified table copy with its header block, declared slot count and populated partitions.
    /// Several entries mean the image packs distinct tables rather than one primary/backup pair.
    /// <para>列出每份 CRC 校验通过的表副本及其头部块、声明槽位数与已用分区数。返回多条说明镜像打包了多张
    /// 互相独立的表，而不是一份主备对。</para>
    /// </summary>
    /// <param name="image">The image bytes to inspect. <para>待检查的镜像字节。</para></param>
    /// <returns>Copies in ascending header order; empty when no header validates. <para>按头部升序排列的副本；没有有效头部时为空。</para></returns>
    public static IReadOnlyList<GptTableCopy> DescribeCopies(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        List<GptTableCopy> copies = [];
        for (var headerOffset = 0L; (headerOffset + GptLayout.HeaderSize) <= image.Length; headerOffset += GptLayout.SectorSize)
        {
            if (image.AsSpan((int)headerOffset, GptLayout.Signature.Length).SequenceEqual(GptLayout.Signature)
                && TryReadCopy(image, headerOffset, out var copy, out var partitions))
            {
                copies.Add(new GptTableCopy((ulong)(headerOffset / GptLayout.SectorSize), copy.EntryCount, partitions.Count));
            }
        }

        return copies;
    }

    /// <summary>
    /// Writes partition entries back in place for every copy that mirrors the reported table or is still
    /// empty; a copy carrying a different populated table is an independent table in the same container and is
    /// preserved. Geometry fields and surrounding bytes are kept and only counts and CRC-32 values change.
    /// <para>对镜像中与上报表一致或仍为空的每个副本原位回写分区表项；携带另一张有效表的副本属于同一容器内的
    /// 独立表，保持原样。几何字段与周边字节保持不变，只更新条目数量与 CRC-32。</para>
    /// </summary>
    /// <param name="image">The source image providing the headers and entry arrays. <para>提供头部与条目数组的源镜像。</para></param>
    /// <param name="partitions">The partition entries to write in array order. <para>按数组顺序写入的分区表项。</para></param>
    /// <returns>A copy of the image with the rewritten entry arrays. <para>改写条目数组后的镜像副本。</para></returns>
    /// <exception cref="ArgumentException">No CRC-verified GPT copy exists, the list is empty, the slot count exceeds <c>1024</c>, or an entry array cannot hold the slots. <para>不存在 CRC 校验通过的 GPT 副本、列表为空、槽位数量超过 <c>1024</c>，或条目数组无法容纳槽位。</para></exception>
    public static byte[] Patch(byte[] image, IReadOnlyList<GptPartition> partitions)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(partitions);
        if (!TryLocate(image, null, out var copies, out var source, out _, out _, out _))
        {
            throw new ArgumentException(
                "No GPT header with a CRC-verified entry array was found. 未找到 CRC 校验通过的 GPT 头与条目数组。",
                nameof(image));
        }

        var targets = PatchTargets(image, copies, source);

        if (partitions.Count == 0)
        {
            throw new ArgumentException("GPT requires at least one partition. GPT 至少需要一个分区。", nameof(partitions));
        }

        var slotCount = Math.Max(copies.Max(copy => (int)copy.EntryCount), partitions.Count);
        if (slotCount > MaximumEntryCount)
        {
            throw new ArgumentException($"GPT partition count {slotCount} exceeds {MaximumEntryCount}. 分区数量超限。", nameof(partitions));
        }

        var arrayBytes = slotCount * GptLayout.EntrySize;
        foreach (var copy in targets)
        {
            if (arrayBytes > FreeBytesAfter(image, copies, copy))
            {
                throw new ArgumentException(
                    $"The GPT entry array at offset {copy.EntriesOffset} cannot hold {slotCount} slots. 分区表槽位已满，无法写入更多分区。",
                    nameof(partitions));
            }
        }

        var result = (byte[])image.Clone();
        foreach (var copy in targets)
        {
            var array = result.AsSpan((int)copy.EntriesOffset, arrayBytes);
            array.Clear();
            for (var i = 0; i < partitions.Count; i++)
            {
                WritePartition(array.Slice(i * GptLayout.EntrySize, GptLayout.EntrySize), partitions[i]);
            }

            var header = result.AsSpan((int)copy.HeaderOffset, copy.HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.EntriesCrcOffset..], Crc32Ieee.Compute(array));
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..], (uint)slotCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.HeaderCrcOffset..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.HeaderCrcOffset..], Crc32Ieee.Compute(header));
        }

        return result;
    }

    /// <summary>
    /// Locates every CRC-verified header, selects the requested copy or the one whose entry array holds the
    /// most partitions, and derives the capacity from that header.
    /// <para>定位全部 CRC 校验通过的头部，选择请求的副本或条目数组中分区最多的一份，并由该头部推导容量。</para>
    /// </summary>
    /// <param name="image">The image bytes to parse. <para>待解析的镜像字节。</para></param>
    /// <param name="preferredHeaderLba">Header block to select, or <see langword="null"/> for the richest copy. <para>要选择的头部块；<see langword="null"/> 表示分区最多的副本。</para></param>
    /// <param name="copies">Every located header with its resolved entry array. <para>每个已定位头部及其解析出的条目数组。</para></param>
    /// <param name="source">The copy whose partitions and capacity are reported. <para>其分区与容量被上报的副本。</para></param>
    /// <param name="partitions">Partitions of <paramref name="source"/>. <para><paramref name="source"/> 的分区。</para></param>
    /// <param name="diskSizeBytes">Capacity covering both the declared alternate block and the last partition. <para>覆盖声明备用块与最后一个分区的容量。</para></param>
    /// <param name="diskGuid">Disk identifier of <paramref name="source"/>. <para><paramref name="source"/> 的磁盘标识。</para></param>
    /// <returns><see langword="true"/> when the requested or richest copy was found. <para>找到请求的副本或分区最多的副本时为 <see langword="true"/>。</para></returns>
    private static bool TryLocate(byte[] image, ulong? preferredHeaderLba, out List<GptCopy> copies,
        out GptCopy source, out List<GptPartition> partitions, out ulong diskSizeBytes, out Guid diskGuid)
    {
        copies = [];
        source = default;
        partitions = [];
        diskSizeBytes = 0;
        diskGuid = Guid.Empty;

        List<(GptCopy Copy, List<GptPartition> Partitions)> found = [];
        for (var headerOffset = 0L; (headerOffset + GptLayout.HeaderSize) <= image.Length; headerOffset += GptLayout.SectorSize)
        {
            if (!image.AsSpan((int)headerOffset, GptLayout.Signature.Length).SequenceEqual(GptLayout.Signature)
                || !TryReadCopy(image, headerOffset, out var copy, out var candidate))
            {
                continue;
            }

            copies.Add(copy);
            found.Add((copy, candidate));
        }

        var selected = -1;
        if (preferredHeaderLba is { } requested)
        {
            for (var index = 0; index < found.Count && selected < 0; index++)
            {
                if (HeaderLbaOf(found[index].Copy) == requested)
                {
                    selected = index;
                }
            }

            if (selected < 0)
            {
                return false;
            }
        }
        else
        {
            for (var index = 0; index < found.Count; index++)
            {
                if (selected < 0 || found[index].Partitions.Count > found[selected].Partitions.Count)
                {
                    selected = index;
                }
            }

            if (selected < 0)
            {
                return false;
            }
        }

        source = found[selected].Copy;
        partitions = found[selected].Partitions;
        var header = image.AsSpan((int)source.HeaderOffset, GptLayout.HeaderSize);
        diskGuid = ReadGuidBigEndian(header[56..]);
        diskSizeBytes = DiskSizeBytes(BinaryPrimitives.ReadUInt64LittleEndian(header[32..]), partitions);
        return true;
    }

    private static ulong HeaderLbaOf(GptCopy copy) => (ulong)(copy.HeaderOffset / GptLayout.SectorSize);

    private static bool TryReadCopy(byte[] image, long headerOffset, out GptCopy copy, out List<GptPartition> partitions)
    {
        copy = default;
        partitions = [];
        var header = image.AsSpan((int)headerOffset, GptLayout.HeaderSize);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        if (headerSize is < GptLayout.HeaderSize or > GptLayout.SectorSize || entrySize != GptLayout.EntrySize
            || entryCount is 0 or > MaximumEntryCount || !HeaderChecksumValid(image, headerOffset, (int)headerSize))
        {
            return false;
        }

        var arrayBytes = (int)(entryCount * entrySize);
        var declared = SectorOffset(BinaryPrimitives.ReadUInt64LittleEndian(header[72..]));
        if (!TryResolveEntryArray(image, headerOffset, declared, arrayBytes, entryCount,
                BinaryPrimitives.ReadUInt32LittleEndian(header[GptLayout.EntriesCrcOffset..]),
                out var entriesOffset, out partitions))
        {
            return false;
        }

        copy = new GptCopy(headerOffset, (int)headerSize, entriesOffset, entryCount);
        return true;
    }

    /// <summary>
    /// Resolves the entry array of one header: the declared LBA offset, the offset immediately following the
    /// header block and a bounded CRC search are verified, and the candidate holding the most partitions wins so
    /// a CRC-valid but empty array cannot mask the populated copy written next to the header.
    /// <para>解析单个头部的条目数组：校验声明 LBA 换算的偏移、紧接头部块之后的偏移与有界 CRC 搜索结果，
    /// 取分区最多的候选项，避免 CRC 合法但为空的数组掩盖写在头部旁边的有效副本。</para>
    /// </summary>
    /// <param name="image">The image bytes to parse. <para>待解析的镜像字节。</para></param>
    /// <param name="headerOffset">Byte offset of the header being paired. <para>待配对头部的字节偏移。</para></param>
    /// <param name="declared">Entry offset derived from the declared entry-array LBA, or <c>-1</c> when it does not fit. <para>由声明的条目数组 LBA 换算的偏移；超出范围时为 <c>-1</c>。</para></param>
    /// <param name="arrayBytes">Size in bytes of the whole entry array. <para>整个条目数组的字节大小。</para></param>
    /// <param name="entryCount">Declared entry slot count. <para>声明的条目槽位数量。</para></param>
    /// <param name="expectedCrc">Stored CRC-32 of the entry array. <para>条目数组已存储的 CRC-32。</para></param>
    /// <param name="entriesOffset">Byte offset of the winning array, or <c>-1</c> on failure. <para>胜出数组的字节偏移，失败时为 <c>-1</c>。</para></param>
    /// <param name="partitions">Partitions read from the winning array. <para>从胜出数组读到的分区。</para></param>
    /// <returns><see langword="true"/> when a CRC-matching array was found. <para>找到 CRC 匹配的数组时为 <see langword="true"/>。</para></returns>
    private static bool TryResolveEntryArray(byte[] image, long headerOffset, long declared, int arrayBytes,
        uint entryCount, uint expectedCrc, out long entriesOffset, out List<GptPartition> partitions)
    {
        entriesOffset = -1;
        partitions = [];
        List<long> candidates = [];
        if (ChecksumMatches(image, declared, arrayBytes, expectedCrc))
        {
            candidates.Add(declared);
        }

        var adjacent = headerOffset + GptLayout.SectorSize;
        if (adjacent != declared && ChecksumMatches(image, adjacent, arrayBytes, expectedCrc))
        {
            candidates.Add(adjacent);
        }

        var scanned = FindEntryArray(image, headerOffset, declared, arrayBytes, expectedCrc);
        if (scanned >= 0 && scanned != declared && scanned != adjacent)
        {
            candidates.Add(scanned);
        }

        foreach (var candidate in candidates)
        {
            var parsed = ReadPartitions(image.AsSpan((int)candidate, arrayBytes), entryCount);
            if (entriesOffset < 0 || parsed.Count > partitions.Count)
            {
                entriesOffset = candidate;
                partitions = parsed;
            }
        }

        return entriesOffset >= 0;
    }

    private static bool HeaderChecksumValid(byte[] image, long headerOffset, int headerSize)
    {
        var header = image.AsSpan((int)headerOffset, headerSize);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(header[GptLayout.HeaderCrcOffset..]);
        Span<byte> scratch = stackalloc byte[GptLayout.SectorSize];
        header.CopyTo(scratch);
        BinaryPrimitives.WriteUInt32LittleEndian(scratch[GptLayout.HeaderCrcOffset..], 0);
        return stored == Crc32Ieee.Compute(scratch[..headerSize]);
    }

    /// <summary>
    /// Locates the entry array: the offset derived from the declared LBA is verified first, then a
    /// bounded sector-aligned window around the header is searched and the closest CRC match wins.
    /// <para>定位条目数组：先校验由声明 LBA 换算出的偏移，失败后在头部附近的有界扇区对齐窗口内搜索，取距离最近的 CRC 匹配。</para>
    /// </summary>
    /// <param name="image">The source image bytes. <para>源镜像字节。</para></param>
    /// <param name="headerOffset">Byte offset of the header being paired with an array. <para>待与数组配对的头部字节偏移。</para></param>
    /// <param name="declared">Entry offset derived from the declared entry-array LBA. <para>由声明的条目数组 LBA 换算出的偏移。</para></param>
    /// <param name="arrayBytes">Size in bytes of the entry array. <para>条目数组的字节大小。</para></param>
    /// <param name="expectedCrc">Stored CRC-32 of the entry array. <para>条目数组已存储的 CRC-32。</para></param>
    /// <returns>The byte offset of a matching array, or <c>-1</c> when none is found. <para>匹配数组的字节偏移，未找到时为 <c>-1</c>。</para></returns>
    private static long FindEntryArray(byte[] image, long headerOffset, long declared, int arrayBytes, uint expectedCrc)
    {
        if (arrayBytes > image.Length)
        {
            return -1;
        }

        if (ChecksumMatches(image, declared, arrayBytes, expectedCrc))
        {
            return declared;
        }

        var low = Math.Max(0, headerOffset - MaximumMetadataDistance) & ~(GptLayout.SectorSize - 1);
        var high = Math.Min(image.Length - arrayBytes, headerOffset + MaximumMetadataDistance);
        var best = -1L;
        var bestDistance = long.MaxValue;
        for (var offset = low; offset <= high; offset += GptLayout.SectorSize)
        {
            if (offset == declared || Math.Abs(offset - headerOffset) >= bestDistance)
            {
                continue;
            }

            if (ChecksumMatches(image, offset, arrayBytes, expectedCrc))
            {
                best = offset;
                bestDistance = Math.Abs(offset - headerOffset);
            }
        }

        return best;
    }

    private static bool ChecksumMatches(byte[] image, long offset, int arrayBytes, uint expectedCrc) =>
        offset >= 0 && (offset + arrayBytes) <= image.Length
        && Crc32Ieee.Compute(image.AsSpan((int)offset, arrayBytes)) == expectedCrc;

    private static long FreeBytesAfter(byte[] image, IReadOnlyList<GptCopy> copies, GptCopy copy)
    {
        var boundary = (long)image.Length;
        foreach (var other in copies)
        {
            if (other.EntriesOffset > copy.EntriesOffset)
            {
                boundary = Math.Min(boundary, other.EntriesOffset);
            }

            if (other.HeaderOffset > copy.EntriesOffset)
            {
                boundary = Math.Min(boundary, other.HeaderOffset);
            }
        }

        return boundary - copy.EntriesOffset;
    }

    /// <summary>
    /// Selects the copies a patch may rewrite: the reported table itself, its byte-identical mirrors and
    /// copies whose entry array is still empty. Distinct populated tables are excluded.
    /// <para>挑选补丁可重写的副本：上报表本身、与其逐字节一致的镜像副本，以及条目数组仍为空的副本；
    /// 内容不同的有效表被排除。</para>
    /// </summary>
    /// <param name="image">The image bytes to inspect. <para>待检查的镜像字节。</para></param>
    /// <param name="copies">Every located table copy. <para>每个已定位的表副本。</para></param>
    /// <param name="source">The copy currently reported as the table. <para>当前作为表上报的副本。</para></param>
    /// <returns>The copies to rewrite, always including <paramref name="source"/>. <para>待重写的副本，始终包含 <paramref name="source"/>。</para></returns>
    private static List<GptCopy> PatchTargets(byte[] image, IReadOnlyList<GptCopy> copies, GptCopy source)
    {
        List<GptCopy> targets = [];
        foreach (var copy in copies)
        {
            if (copy.EntriesOffset == source.EntriesOffset
                || IsEmptyEntryArray(image, copy)
                || MirrorsEntryArray(image, copy, source))
            {
                targets.Add(copy);
            }
        }

        return targets;
    }

    private static bool IsEmptyEntryArray(byte[] image, GptCopy copy) =>
        !image.AsSpan((int)copy.EntriesOffset, (int)copy.EntryCount * GptLayout.EntrySize).ContainsAnyExcept((byte)0);

    private static bool MirrorsEntryArray(byte[] image, GptCopy copy, GptCopy source) =>
        copy.EntryCount == source.EntryCount
        && image.AsSpan((int)copy.EntriesOffset, (int)copy.EntryCount * GptLayout.EntrySize)
            .SequenceEqual(image.AsSpan((int)source.EntriesOffset, (int)source.EntryCount * GptLayout.EntrySize));

    private static List<GptPartition> ReadPartitions(ReadOnlySpan<byte> array, uint entryCount)
    {
        var list = new List<GptPartition>((int)entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var slot = array.Slice(i * GptLayout.EntrySize, GptLayout.EntrySize);
            var typeGuid = ReadGuidBigEndian(slot);
            var first = BinaryPrimitives.ReadUInt64LittleEndian(slot[32..]);
            var last = BinaryPrimitives.ReadUInt64LittleEndian(slot[40..]);
            var name = ReadName(slot[56..]);

            if (typeGuid == Guid.Empty && name.Length == 0 && first == 0 && last == 0)
            {
                continue;
            }

            list.Add(new GptPartition(
                i,
                typeGuid,
                ReadGuidBigEndian(slot[16..]),
                first,
                last,
                BinaryPrimitives.ReadUInt64LittleEndian(slot[48..]),
                name,
                ByteOffsetOf(first),
                ByteLengthOf(first, last)));
        }

        return list;
    }

    private static void WritePartition(Span<byte> slot, GptPartition partition)
    {
        WriteGuidBigEndian(slot, partition.TypeId);
        WriteGuidBigEndian(slot[16..], partition.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], partition.FirstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], partition.LastLba);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[48..], partition.Attributes);
        WriteName(slot[56..], partition.Name);
    }

    /// <summary>
    /// Computes the capacity reported for a table: the declared alternate block wins unless the last partition
    /// extends past it, which happens for table-only images that leave the alternate block at zero.
    /// <para>计算表上报的容量：声明备用块优先，但最后一个分区越过它时以分区结束位置为准；仅含分区表的镜像
    /// 常把备用块留为零。</para>
    /// </summary>
    /// <param name="alternateLba">Alternate header block recorded by the source header; zero means unknown. <para>源头部记录的备用头块；零表示未知。</para></param>
    /// <param name="partitions">Partitions of the reported table. <para>被上报表的分区。</para></param>
    /// <returns>Capacity in bytes, saturating instead of overflowing. <para>以字节计的容量，溢出时饱和。</para></returns>
    private static ulong DiskSizeBytes(ulong alternateLba, IReadOnlyList<GptPartition> partitions)
    {
        const ulong Limit = (ulong)long.MaxValue / (ulong)GptLayout.SectorSize;
        var declared = alternateLba >= Limit
            ? (ulong)long.MaxValue
            : (alternateLba + 1) * (ulong)GptLayout.SectorSize;
        if (partitions.Count == 0)
        {
            return declared;
        }

        var last = partitions.Max(partition => partition.LastLba);
        var occupied = last >= Limit
            ? (ulong)long.MaxValue
            : (last + 1) * (ulong)GptLayout.SectorSize;
        return Math.Max(declared, occupied);
    }

    private static long ByteOffsetOf(ulong firstLba) =>
        firstLba > ((ulong)long.MaxValue / (ulong)GptLayout.SectorSize)
            ? long.MaxValue
            : (long)(firstLba * (ulong)GptLayout.SectorSize);

    private static long ByteLengthOf(ulong firstLba, ulong lastLba)
    {
        if (lastLba < firstLba)
        {
            return 0;
        }

        var sectors = lastLba - firstLba;
        if (sectors >= ((ulong)long.MaxValue / (ulong)GptLayout.SectorSize))
        {
            return long.MaxValue;
        }

        sectors += 1;
        return sectors > ((ulong)long.MaxValue / (ulong)GptLayout.SectorSize)
            ? long.MaxValue
            : (long)(sectors * (ulong)GptLayout.SectorSize);
    }

    private static long SectorOffset(ulong lba) =>
        lba > ((ulong)long.MaxValue / (ulong)GptLayout.SectorSize) ? -1 : (long)(lba * (ulong)GptLayout.SectorSize);

    private static string ComputeFingerprint(byte[] image, IReadOnlyList<GptCopy> copies, GptCopy primary)
    {
        using var hash = Sha256.CreateAccumulator();
        Span<byte> digest = stackalloc byte[32];
        foreach (var copy in copies)
        {
            var headerBytes = Math.Min(GptLayout.SectorSize, image.Length - (int)copy.HeaderOffset);
            Sha256.ComputeHash(image.AsSpan((int)copy.HeaderOffset, headerBytes), digest);
            hash.Append(digest);
        }

        hash.Append(image.AsSpan((int)primary.EntriesOffset, (int)primary.EntryCount * GptLayout.EntrySize));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteGuidBigEndian(Span<byte> target, Guid guid)
    {
        var bytes = guid.ToByteArray();
        BinaryPrimitives.WriteUInt32BigEndian(target, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        BinaryPrimitives.WriteUInt16BigEndian(target[4..], BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]));
        BinaryPrimitives.WriteUInt16BigEndian(target[6..], BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
        bytes.AsSpan(8, 8).CopyTo(target[8..]);
    }

    private static Guid ReadGuidBigEndian(ReadOnlySpan<byte> source)
    {
        var a = BinaryPrimitives.ReadUInt32BigEndian(source);
        var b = BinaryPrimitives.ReadUInt16BigEndian(source[4..]);
        var c = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        return new Guid((int)a, (short)b, (short)c, source.Slice(8, 8).ToArray());
    }

    private static void WriteName(Span<byte> target, string name)
    {
        var length = Math.Min(name.Length, GptLayout.MaxNameUnits);
        Encoding.Unicode.GetBytes(name.AsSpan(0, length), target[..(length * 2)]);
    }

    private static string ReadName(ReadOnlySpan<byte> source)
    {
        var units = source[..(GptLayout.MaxNameUnits * 2)];
        var end = 0;
        while (end + 1 < units.Length && (units[end] != 0 || units[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(units[..end]);
    }

    /// <summary>
    /// A header and its entry array located inside the source image.
    /// <para>头部与其条目数组在源镜像中的定位。</para>
    /// </summary>
    /// <param name="HeaderOffset">Byte offset of the header within the image. <para>头部在镜像内的字节偏移。</para></param>
    /// <param name="HeaderSize">Header size in bytes as recorded by the header. <para>头部自记的字节大小。</para></param>
    /// <param name="EntriesOffset">Byte offset of the entry array within the image. <para>条目数组在镜像内的字节偏移。</para></param>
    /// <param name="EntryCount">Declared number of partition entries. <para>声明的分区表项数量。</para></param>
    private readonly record struct GptCopy(long HeaderOffset, int HeaderSize, long EntriesOffset, uint EntryCount);
}