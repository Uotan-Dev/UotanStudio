using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects which copy of the device's own GPT one programmed extent carries.
/// <para>标识某个已编程范围承载设备自身 GPT 的哪一份副本。</para></summary>
public enum MtkGptCopy
{
    /// <summary>The protective MBR, primary header and primary entry array.
    /// <para>保护性 MBR、主头部与主条目数组。</para></summary>
    Primary,
    /// <summary>The backup entry array and trailing header.
    /// <para>备份条目数组与末尾头部。</para></summary>
    Backup
}

/// <summary>
/// One block-aligned metadata extent a GPT write programs, with the exact bytes it carries.
/// <para>GPT 写入所编程的一段按块对齐的元数据范围，及其承载的精确字节。</para>
/// </summary>
/// <param name="Copy">Which GPT copy the extent carries. <para>该范围承载的 GPT 副本。</para></param>
/// <param name="RegionName">The canonical region token the owning family addresses.
/// <para>所属家族用于寻址的规范区域标记。</para></param>
/// <param name="PhysicalIdentifier">The numeric region identifier, or null for a named-only XML region.
/// <para>数值区域标识符；仅按名称寻址的 XML 区域为 null。</para></param>
/// <param name="Offset">The region-relative byte offset of the extent. <para>范围相对于区域的字节偏移。</para></param>
/// <param name="Length">The exact byte count of <paramref name="Content"/>.
/// <para><paramref name="Content"/> 的精确字节数。</para></param>
/// <param name="Content">The serialized metadata bytes; callers must treat the array as read-only.
/// <para>已序列化的元数据字节；调用方必须将该数组视为只读。</para></param>
public sealed record MtkGptWriteExtent(MtkGptCopy Copy, string RegionName, uint? PhysicalIdentifier, long Offset,
    long Length, byte[] Content);

/// <summary>
/// Plans the write-back of a device's own primary and backup GPT copies from one validated snapshot, reading them back through the per-family clients.
/// <para>规划依据一份已校验快照写回设备自身的主、备 GPT 副本，并经各协议族的客户端回读。</para>
/// </summary>
/// <remarks>
/// <para>Deliberate scope limit: this planner only re-programs the table the device itself reported. It
/// accepts no foreign GPT, rewrites no geometry, changes no partition payload and never resizes a region;
/// a table whose declared block count exceeds the region it was read from is refused before any byte is
/// produced. The extent bytes are re-serialized from the validated table, so header and array layout fields,
/// entry contents and the disk identifier round-trip, while reserved bytes the model does not carry are
/// emitted as the canonical zeros the reader also enforces.</para>
/// <para>刻意的范围限制：本规划器只重新编程设备自己上报的分区表。它不接受外部 GPT、不改写几何信息、不改变任何
/// 分区数据，也不会调整区域大小；声明块数超出其来源区域的表会在生成任何字节之前被拒绝。范围字节由已校验的表
/// 重新序列化得到，因此头部与数组布局字段、条目内容与磁盘标识符会原样往返，而模型未携带的保留字节则按读取器
/// 同样强制的规范零值输出。</para>
/// <para>Reference correspondence, per extent: the primary copy travels from block zero, which is what the
/// reference does when a table is flashed back as the <c>gpt</c> partition
/// (<c>Library/DA/mtk_da_handler.py:352-357</c> for <c>w</c> and <c>Library/DA/mtk_da_handler.py:404-413</c>
/// for <c>wl</c>, both calling <c>writeflash(addr=0, length=&lt;file size&gt;)</c>). The reference's own table
/// write stops there: it never programs a backup copy through the download agent, and the only backup addressing
/// it defines is a read at the header-declared location
/// (<c>Library/partition.py:131-133</c>, <c>addr = header.backup_lba * pagesize</c>), so the backup extent here
/// is programmed at the block the table itself declares, never at a slot measured from the reported region end.
/// The region-end form only appears in the reference's <c>generate_rawprogram</c> template
/// (<c>Library/gpt.py:312-324</c>, <c>start_sector = NUM_DISK_SECTORS - sectors</c>), which has no caller.</para>
/// <para>逐范围的参考对应关系：主副本从块零开始传输，这正是参考把分区表作为 <c>gpt</c> 分区写回时的做法
/// （<c>Library/DA/mtk_da_handler.py:352-357</c> 的 <c>w</c> 与
/// <c>Library/DA/mtk_da_handler.py:404-413</c> 的 <c>wl</c>，两者都调用
/// <c>writeflash(addr=0, length=&lt;文件大小&gt;)</c>）。参考自身的分区表写入到此为止：它从不通过下载代理编程
/// 备份副本，且它唯一定义的备份寻址是按头部声明位置进行的读取
/// （<c>Library/partition.py:131-133</c>，<c>addr = header.backup_lba * pagesize</c>），因此这里的备份范围编程在
/// 分区表自己声明的块上，绝不取自从上报区域末端量出的槽位。按区域末端定位的写法只出现在参考的
/// <c>generate_rawprogram</c> 模板里（<c>Library/gpt.py:312-324</c>，
/// <c>start_sector = NUM_DISK_SECTORS - sectors</c>），而该模板没有任何调用者。</para>
/// </remarks>
public static class MtkGptWritePlanner
{
    private const int LegacyGptBlockSize = 512;

    /// <summary>Plans both XFlash GPT extents for one validated snapshot.
    /// <para>为一个已校验的 XFlash 快照规划两份 GPT 范围。</para></summary>
    /// <param name="snapshot">The observation whose table, region and geometry are written back.
    /// <para>要写回其分区表、区域与几何信息的观测结果。</para></param>
    /// <returns>The primary extent followed by the backup extent.<para>返回主范围，其后是备份范围。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The snapshot no longer describes a reported region.
    /// <para>该快照不再描述某个已上报的区域。</para></exception>
    /// <exception cref="InvalidDataException">The table's block size disagrees with the region, its declared capacity exceeds the region, or its layout violates a GPT invariant.
    /// <para>表的块大小与区域不一致、声明容量超出区域，或布局违反 GPT 不变量。</para></exception>
    /// <exception cref="OverflowException">A block product or byte offset exceeds the supported range.
    /// <para>块乘积或字节偏移超出支持范围。</para></exception>
    public static IReadOnlyList<MtkGptWriteExtent> Plan(MtkGptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var storage = snapshot.Storage;
        var region = storage.Regions.FirstOrDefault(candidate => candidate.PartitionId == snapshot.PartitionId)
            ?? throw new InvalidOperationException(
                "The XFlash GPT snapshot no longer describes a reported physical region.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException(
                "The XFlash GPT snapshot addresses an authenticated region, which is not ordinary block storage.");
        }

        var capability = MtkStorageCapabilitySnapshot.FromXFlash(storage);
        var canonicalName = capability.OrdinaryRegions
            .FirstOrDefault(candidate => candidate.PhysicalIdentifier == snapshot.PartitionId)?.CanonicalName
            ?? $"XFLASH-{storage.Kind}-PARTITION-{snapshot.PartitionId}";
        return Build(snapshot.Table, storage.BlockSize, region.CapacityBytes, canonicalName,
            snapshot.PartitionId);
    }

    /// <summary>Plans both Legacy GPT extents for one validated snapshot.
    /// <para>为一个已校验的 Legacy 快照规划两份 GPT 范围。</para></summary>
    /// <param name="snapshot">The observation whose table, region and inventory are written back.
    /// <para>要写回其分区表、区域与清单信息的观测结果。</para></param>
    /// <returns>The primary extent followed by the backup extent.<para>返回主范围，其后是备份范围。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The snapshot no longer describes a reported region.
    /// <para>该快照不再描述某个已上报的区域。</para></exception>
    /// <exception cref="NotSupportedException">The Legacy agent was initialized for storage whose block size is not the fixed Legacy GPT geometry.
    /// <para>Legacy 代理初始化的存储不具备 Legacy GPT 的固定几何。</para></exception>
    /// <exception cref="InvalidDataException">The table's block size disagrees with the region, its declared capacity exceeds the region, or its layout violates a GPT invariant.
    /// <para>表的块大小与区域不一致、声明容量超出区域，或布局违反 GPT 不变量。</para></exception>
    /// <exception cref="OverflowException">A block product or byte offset exceeds the supported range.
    /// <para>块乘积或字节偏移超出支持范围。</para></exception>
    public static IReadOnlyList<MtkGptWriteExtent> Plan(MtkLegacyGptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var storage = snapshot.Storage;
        if (storage.Kind != MtkXFlashStorageKind.Emmc)
        {
            throw new NotSupportedException(
                $"The Legacy download agent reports {storage.Kind} storage, which has no fixed 512-byte GPT " +
                "block geometry to write back.");
        }

        var region = storage.Regions.FirstOrDefault(candidate => candidate.PartitionId == snapshot.PartitionId)
            ?? throw new InvalidOperationException(
                "The Legacy GPT snapshot no longer describes a reported physical region.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException(
                "The Legacy GPT snapshot addresses an authenticated region, which requires a separate protocol.");
        }

        var capability = MtkStorageCapabilitySnapshot.FromLegacy(storage);
        var canonicalName = capability.OrdinaryRegions
            .FirstOrDefault(candidate => candidate.PhysicalIdentifier == snapshot.PartitionId)?.CanonicalName
            ?? $"LEGACY-EMMC-PARTITION-{snapshot.PartitionId}";
        return Build(snapshot.Table, LegacyGptBlockSize, region.CapacityBytes, canonicalName,
            snapshot.PartitionId);
    }

    /// <summary>Plans both XML GPT extents for one validated snapshot.
    /// <para>为一个已校验的 XML 快照规划两份 GPT 范围。</para></summary>
    /// <param name="snapshot">The observation whose table, named region and geometry are written back.
    /// <para>要写回其分区表、命名区域与几何信息的观测结果。</para></param>
    /// <returns>The primary extent followed by the backup extent.<para>返回主范围，其后是备份范围。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The snapshot no longer describes a reported region.
    /// <para>该快照不再描述某个已上报的区域。</para></exception>
    /// <exception cref="NotSupportedException">The named region is reserved for authenticated access.
    /// <para>该命名区域为认证访问保留。</para></exception>
    /// <exception cref="InvalidDataException">The table's block size disagrees with the region, its declared capacity exceeds the region, or its layout violates a GPT invariant.
    /// <para>表的块大小与区域不一致、声明容量超出区域，或布局违反 GPT 不变量。</para></exception>
    /// <exception cref="OverflowException">A block product or byte offset exceeds the supported range.
    /// <para>块乘积或字节偏移超出支持范围。</para></exception>
    public static IReadOnlyList<MtkGptWriteExtent> Plan(MtkXmlGptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var storage = snapshot.Storage;
        var region = storage.Regions.FirstOrDefault(candidate => candidate.Name == snapshot.RegionName)
            ?? throw new InvalidOperationException(
                "The XML GPT snapshot no longer describes a reported physical region.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException(
                "The XML GPT snapshot addresses a region reserved for a separate authenticated protocol.");
        }

        return Build(snapshot.Table, storage.BlockSize, region.CapacityBytes, snapshot.RegionName, null);
    }

    private static IReadOnlyList<MtkGptWriteExtent> Build(GptTable table, int blockSize, long capacityBytes,
        string regionName, uint? physicalIdentifier)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        if (table.BlockSize != blockSize)
        {
            throw new InvalidDataException(
                $"The GPT table was read with {table.BlockSize}-byte blocks while the {regionName} region " +
                $"reports {blockSize}-byte blocks.");
        }

        if (table.BlockCount == 0
            || (ulong)capacityBytes / (uint)blockSize < table.BlockCount
            || checked((long)table.BlockCount * blockSize) > capacityBytes)
        {
            throw new InvalidDataException(
                $"The GPT table claims {table.BlockCount} blocks, which does not fit inside the " +
                $"{capacityBytes} bytes the {regionName} region reports.");
        }

        var extents = GptDeviceImage.BuildExtents(table, GptCopySelection.Both);
        var planned = new List<MtkGptWriteExtent>(extents.Count);
        foreach (var extent in extents)
        {
            var offset = checked((long)extent.StartLba * blockSize);
            var length = checked((long)extent.SectorCount * blockSize);
            if (extent.Bytes.Length != length)
            {
                throw new InvalidDataException(
                    "The serialized GPT extent does not match the block count it declares.");
            }

            if (offset < 0 || length <= 0 || offset > capacityBytes || length > capacityBytes - offset)
            {
                throw new InvalidDataException(
                    $"The GPT extent at byte {offset} for {length} bytes exceeds the {capacityBytes} bytes " +
                    $"reported for the {regionName} region.");
            }

            planned.Add(new MtkGptWriteExtent(extent.StartLba == 0 ? MtkGptCopy.Primary : MtkGptCopy.Backup,
                regionName, physicalIdentifier, offset, length, extent.Bytes));
        }

        return planned;
    }
}