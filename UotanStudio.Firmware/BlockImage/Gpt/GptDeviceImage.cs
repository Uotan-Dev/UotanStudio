using System.IO.Hashing;

namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>One contiguous block-aligned device range carrying part of a GPT layout.
/// <para>携带部分 GPT 布局的一段连续、按块对齐的设备范围。</para></summary>
/// <param name="StartLba">First logical block of the extent.
/// <para>该范围的第一个逻辑块。</para></param>
/// <param name="SectorCount">Number of logical blocks in <paramref name="Bytes"/>.
/// <para><paramref name="Bytes"/> 所含的逻辑块数量。</para></param>
/// <param name="Bytes">The exact bytes to program, already block-aligned.
/// <para>需要编程的精确字节，已按块对齐。</para></param>
public sealed record GptDeviceExtent(ulong StartLba, uint SectorCount, byte[] Bytes);

/// <summary>Selects which copy of a GPT layout a caller wants serialized for programming.
/// <para>选择调用方希望序列化为可编程内容的 GPT 副本。</para></summary>
public enum GptCopySelection
{
    /// <summary>Only the primary range that starts at LBA 0.
    /// <para>仅从 LBA 0 开始的主范围。</para></summary>
    Primary,
    /// <summary>Only the backup range ending with the trailing header.
    /// <para>仅以最末头部收尾的备份范围。</para></summary>
    Backup,
    /// <summary>Both ranges, primary first. <para>两个范围，主范围在前。</para></summary>
    Both
}

/// <summary>
/// Serializes the protective MBR, headers and entry arrays into the two device ranges a flashing tool programs: the primary range at LBA 0 and the backup range with the secondary array plus the trailing header. The result never covers the usable partition area.
/// <para>把保护性 MBR、头部与条目数组序列化到刷写工具写入的两个设备区：LBA 0 起的主区，以及含次级数组与尾部头部的备区。结果绝不覆盖可用分区区域。</para>
/// </summary>
public static class GptDeviceImage
{
    /// <summary>Builds both device extents for one validated table.
    /// <para>为一个已校验的表构建两个设备范围。</para></summary>
    /// <param name="table">The table to serialize; its geometry, layout LBAs and entry dimensions are
    /// validated first. <para>待序列化的表；先校验其几何信息、布局 LBA 与条目维度。</para></param>
    /// <returns>The primary extent at LBA 0 followed by the backup extent, each with its exact byte content.
    /// <para>返回位于 LBA 0 的主范围，随后是备份范围，各自携带精确字节内容。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.
    /// <para><paramref name="table"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The table violates the GPT invariants, or its primary and
    /// backup ranges would overlap. <para>该表违反 GPT 不变量，或主备份范围会互相重叠。</para></exception>
    /// <exception cref="OverflowException">A byte count or block offset exceeds the supported range.
    /// <para>字节数或块偏移超出支持范围。</para></exception>
    public static IReadOnlyList<GptDeviceExtent> BuildExtents(GptTable table) =>
        BuildExtents(table, GptCopySelection.Both);

    /// <summary>Builds the selected device extents for one validated table.
    /// <para>为一个已校验的表构建所选的设备范围。</para></summary>
    /// <param name="table">The table to serialize; its geometry, layout LBAs and entry dimensions are
    /// validated first. <para>待序列化的表；先校验其几何信息、布局 LBA 与条目维度。</para></param>
    /// <param name="copies">Which copy or copies to emit; both headers always carry the same entry array and
    /// disk identifier, so a single-copy selection still writes a self-consistent range.
    /// <para>要输出的副本；两个头部始终携带相同的条目数组与磁盘标识符，因此仅选一个副本时其范围仍自洽。</para></param>
    /// <returns>The requested extents, primary first when it is included.
    /// <para>返回请求的范围；包含主范围时它排在前面。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.
    /// <para><paramref name="table"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The table violates the GPT invariants, or its primary and
    /// backup ranges would overlap. <para>该表违反 GPT 不变量，或主备份范围会互相重叠。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="copies"/> is not a defined value.
    /// <para><paramref name="copies"/> 不是已定义的值。</para></exception>
    /// <exception cref="OverflowException">A byte count or block offset exceeds the supported range.
    /// <para>字节数或块偏移超出支持范围。</para></exception>
    public static IReadOnlyList<GptDeviceExtent> BuildExtents(GptTable table, GptCopySelection copies)
    {
        if (!Enum.IsDefined(copies))
        {
            throw new ArgumentOutOfRangeException(nameof(copies));
        }

        ArgumentNullException.ThrowIfNull(table);
        GptTableWriter.ValidateTable(table);
        var blockSize = (ulong)table.BlockSize;
        var arrayBytes = checked((ulong)table.PartitionEntryCount * table.PartitionEntrySize);
        var arrayBlocks = (arrayBytes + blockSize - 1) / blockSize;
        var primaryEnd = checked(table.PrimaryEntryArrayLba + arrayBlocks);
        if (table.BackupEntryArrayLba < primaryEnd || table.BlockCount - 1 < table.BackupEntryArrayLba + arrayBlocks)
        {
            throw new InvalidDataException(
                "The GPT entry arrays overlap or leave no room for the backup header.");
        }

        var entryArray = GptTableWriter.BuildEntryArray(table);
        var arrayCrc = Crc32.HashToUInt32(entryArray);
        var primaryBlocks = checked(table.PrimaryEntryArrayLba + arrayBlocks);
        var primaryBytes = new byte[checked((int)primaryBlocks * table.BlockSize)];
        GptTableWriter.BuildProtectiveMbr(table.BlockCount).CopyTo(primaryBytes, 0);
        GptTableWriter.BuildHeaderBlock(table, 1, table.BlockCount - 1, table.PrimaryEntryArrayLba, arrayCrc)
            .CopyTo(primaryBytes, table.BlockSize);
        entryArray.CopyTo(primaryBytes, checked((int)table.PrimaryEntryArrayLba * table.BlockSize));
        var backupBlocks = checked(table.BlockCount - table.BackupEntryArrayLba);
        var backupBytes = new byte[checked((int)backupBlocks * table.BlockSize)];
        entryArray.CopyTo(backupBytes, 0);
        GptTableWriter.BuildHeaderBlock(table, table.BlockCount - 1, 1, table.BackupEntryArrayLba, arrayCrc)
            .CopyTo(backupBytes, checked((int)(backupBlocks - 1) * table.BlockSize));
        var primary = new GptDeviceExtent(0, (uint)primaryBlocks, primaryBytes);
        var backup = new GptDeviceExtent(table.BackupEntryArrayLba, (uint)backupBlocks, backupBytes);
        return copies switch
        {
            GptCopySelection.Primary => [primary],
            GptCopySelection.Backup => [backup],
            _ => [primary, backup]
        };
    }
}