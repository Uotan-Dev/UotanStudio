namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const int MbrSectorBytes = MtkMbrSnapshot.SectorSize;
    private const int MbrReadTimeoutMs = 60000;

    /// <summary>Reads the Master Boot Record of one Legacy region and follows its extended chain, without writing anything.
    /// <para>读取一个 Legacy 区域的主引导记录并沿其扩展链遍历，不写入任何内容。</para>
    /// </summary>
    /// <remarks>
    /// The device is asked for exactly one sector at a time, so a missing or corrupt record is reported at the hop
    /// that produced it instead of silently truncating the list. Every extent is validated against the region
    /// capacity, and the chain is bounded by hop count and by visited-set membership, so a corrupt loop cannot spin
    /// the reader. Nothing is repaired, guessed or replayed: an invalid table surfaces as
    /// <see cref="InvalidDataException"/>. This overload assumes the record starts at the region's first byte; use
    /// <see cref="ReadLegacyPartitionTableAsync"/> when the region may prefix its table with a vendor header.
    /// <para>每次只向设备请求一个扇区，因此缺失或损坏的记录会在产生它的那一跳上报，而不是静默截断列表。每个区间都
    /// 会按区域容量校验，链同时受跳数与已访问集合约束，损坏的环无法让读取器空转。不做修复、猜测或重放：无效的表以
    /// <see cref="InvalidDataException"/> 呈现。此重载假定记录从区域首字节开始；当区域可能以厂商头部作为分区表前缀
    /// 时，请使用 <see cref="ReadLegacyPartitionTableAsync"/>。</para>
    /// </remarks>
    /// <param name="partitionId">Explicit discovered Legacy physical region, not a partition index.
    /// <para>显式指定的已发现 Legacy 物理区域，不是分区索引。</para></param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange.
    /// <para>取消信号会传播到每一次存储交换。</para></param>
    /// <returns>A validated MBR snapshot bound to the selected region.
    /// <para>与所选区域绑定的已校验 MBR 快照。</para></returns>
    /// <exception cref="InvalidOperationException">Storage is not ready, has not been discovered, or <paramref name="partitionId"/> is not a discovered region.
    /// <para>存储未就绪、尚未发现，或 <paramref name="partitionId"/> 不是已发现的区域。</para></exception>
    /// <exception cref="InvalidDataException">A sector read was incomplete, the boot signature was absent, an extent exceeded the region, or the chain was invalid.
    /// <para>扇区读取不完整、引导签名缺失、区间超出区域，或链无效。</para></exception>
    /// <exception cref="NotSupportedException">NOR/NAND page geometry does not define a block-device layout.
    /// <para>NOR/NAND 页几何信息未定义块设备布局。</para></exception>
    /// <exception cref="MtkProtocolException">A storage exchange faulted the session.
    /// <para>某次存储交换使会话进入 fault 状态。</para></exception>
    public async Task<MtkMbrSnapshot> ReadLegacyMbrAsync(byte partitionId,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLegacyMbrOwnedAsync(partitionId, 0, null, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads an MBR that may start part-way into a Legacy region, treating the table base as LBA 0.
    /// <para>读取可能起始于 Legacy 区域中段的 MBR，并把分区表基址视为 LBA 0。</para>
    /// </summary>
    /// <param name="partitionId">Explicit discovered Legacy physical region. <para>显式指定的已发现 Legacy 物理区域。</para></param>
    /// <param name="tableBaseOffset">Region-relative byte offset of the record, aligned to 512.
    /// <para>记录在区域内相对于区域的字节偏移，按 512 对齐。</para></param>
    /// <param name="prefetchedWindow">Bytes already read at <paramref name="prefetchedWindowOffset"/> during table
    /// discovery, reused so the single-sector reads that fail on some agents are avoided.
    /// <para>分区表发现时在 <paramref name="prefetchedWindowOffset"/> 处已读取的字节；复用它们可避开某些代理上会失败的单扇区读取。</para></param>
    /// <param name="prefetchedWindowOffset">Region-relative offset of the first byte of <paramref name="prefetchedWindow"/>.
    /// <para><paramref name="prefetchedWindow"/> 首字节相对于区域的偏移。</para></param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange. <para>取消信号会传播到每一次存储交换。</para></param>
    /// <returns>A validated MBR snapshot whose logical block numbers are relative to <paramref name="tableBaseOffset"/>.
    /// <para>已校验的 MBR 快照，其逻辑块号相对于 <paramref name="tableBaseOffset"/>。</para></returns>
    private async Task<MtkMbrSnapshot> ReadLegacyMbrOwnedAsync(byte partitionId, long tableBaseOffset,
        byte[]? prefetchedWindow, long prefetchedWindowOffset, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.LegacyStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = LegacyStorage ?? throw new InvalidOperationException("Legacy storage has not been discovered.");
        if (storage.Kind is MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException("Raw flash page geometry does not define a block-device layout.");
        }

        storage.ValidateRange(partitionId, tableBaseOffset, MbrSectorBytes);
        var region = storage.Regions.Single(candidate => candidate.PartitionId == partitionId);
        var totalSectors = checked((uint)((region.CapacityBytes - tableBaseOffset) / MbrSectorBytes));
        if (totalSectors == 0)
        {
            throw new InvalidDataException("The selected Legacy region reports no addressable sectors.");
        }

        var sector = new byte[MbrSectorBytes];
        await ReadMbrSectorAsync(partitionId, tableBaseOffset, prefetchedWindow, prefetchedWindowOffset, sector,
            cancellationToken).ConfigureAwait(false);
        if (!MtkMbrSnapshot.HasBootSignature(sector))
        {
            throw new InvalidDataException(
                $"The Legacy region has no MBR at byte offset {tableBaseOffset}: the 0x55AA boot signature is absent " +
                $"(actual {sector[MbrSectorBytes - 2]:X2} {sector[MbrSectorBytes - 1]:X2}, " +
                $"head {Convert.ToHexString(sector.AsSpan(0, 16))}, " +
                $"tail {Convert.ToHexString(sector.AsSpan(MbrSectorBytes - 16))}).");
        }

        var primary = MtkMbrSnapshot.ReadEntries(sector);
        ValidateExtents("the primary table", primary, totalSectors, extendedBase: 0);

        var logical = new List<MtkMbrLogicalPartition>();
        var chainRecords = new List<MtkMbrPartition>();
        var extended = primary.FirstOrDefault(static entry => entry.IsExtended);
        if (extended is not null)
        {
            var visited = new HashSet<uint>();
            var next = extended.FirstLba;
            for (var hop = 0; ; hop++)
            {
                if (hop >= MtkMbrSnapshot.MaximumChainHops)
                {
                    throw new InvalidDataException(
                        $"The extended partition chain exceeded {MtkMbrSnapshot.MaximumChainHops} hops.");
                }

                if (next == 0 || next >= totalSectors)
                {
                    throw new InvalidDataException($"An extended boot record points outside the region at LBA {next}.");
                }

                if (!visited.Add(next))
                {
                    throw new InvalidDataException($"The extended partition chain revisits LBA {next}.");
                }

                var chainOffset = checked(tableBaseOffset + (long)next * MbrSectorBytes);
                await ReadMbrSectorAsync(partitionId, chainOffset, prefetchedWindow, prefetchedWindowOffset, sector,
                    cancellationToken).ConfigureAwait(false);
                if (!MtkMbrSnapshot.HasBootSignature(sector))
                {
                    throw new InvalidDataException($"The extended boot record at LBA {next} has no 0x55AA signature.");
                }

                var record = MtkMbrSnapshot.ReadEntries(sector);
                chainRecords.AddRange(record);
                for (var index = 0; index < record.Length; index++)
                {
                    var entry = record[index];
                    if (!entry.IsPopulated)
                    {
                        continue;
                    }

                    if (entry.IsExtended)
                    {
                        // The first slot of an EBR links to the next EBR, and its LBA is relative to the container.
                        // EBR 的第一个槽位链接下一个 EBR，其 LBA 相对于扩展容器起始位置。
                        if (index != 0)
                        {
                            continue;
                        }

                        var following = checked(extended.FirstLba + entry.FirstLba);
                        if (following == 0)
                        {
                            throw new InvalidDataException(
                                $"The extended boot record at LBA {next} declares a null link.");
                        }

                        next = following;
                        continue;
                    }

                    var absolute = checked(next + entry.FirstLba);
                    var sectorCount = MtkMbrSnapshot.ResolveExtentSectorCount(
                        entry.FirstLba, entry.SectorCount, absolute, totalSectors);
                    if (sectorCount == 0)
                    {
                        throw new InvalidDataException(
                            $"The logical partition at LBA {absolute} in the record at LBA {next} declares no usable sectors.");
                    }

                    logical.Add(new MtkMbrLogicalPartition(logical.Count, entry.Status, entry.PartitionType,
                        absolute, sectorCount, next));
                }

                if (next == extended.FirstLba || !record.Any(static entry => entry.IsExtended))
                {
                    break;
                }
            }
        }

        ThrowIfDisposed();
        return new MtkMbrSnapshot(partitionId, totalSectors, primary, logical, chainRecords);
    }

    private static void ValidateExtents(string table, MtkMbrPartition[] entries, uint totalSectors, uint extendedBase)
    {
        foreach (var entry in entries)
        {
            if (!entry.IsPopulated)
            {
                continue;
            }

            if (entry.IsExtended)
            {
                continue;
            }

            var first = extendedBase == 0 ? entry.FirstLba : checked(extendedBase + entry.FirstLba);
            var sectorCount = MtkMbrSnapshot.ResolveExtentSectorCount(
                entry.FirstLba, entry.SectorCount, first, totalSectors);
            if (sectorCount == 0)
            {
                throw new InvalidDataException(
                    $"{table} declares a partition at LBA {first} with no usable sectors.");
            }
        }
    }

    private async Task ReadMbrSectorAsync(byte partitionId, long offset, byte[]? window, long windowOffset,
        byte[] destination, CancellationToken cancellationToken)
    {
        if (window is not null && offset >= windowOffset && offset - windowOffset >= 0 &&
            offset - windowOffset + destination.Length <= window.Length)
        {
            window.AsSpan((int)(offset - windowOffset), destination.Length).CopyTo(destination);
            return;
        }

        await ReadSectorOwnedAsync(partitionId, offset, destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadSectorOwnedAsync(byte partitionId, long offset, byte[] destination,
        CancellationToken cancellationToken)
    {
        using var output = new UotanStudio.Comm.Protocols.Common.FixedBufferWriteStream(destination);
        await TransferLegacyStorageOwnedAsync(false, partitionId, offset, destination.Length, output,
            MbrReadTimeoutMs, null, cancellationToken, nor: false).ConfigureAwait(false);
        if (output.Written != destination.Length)
        {
            throw new InvalidDataException(
                $"The Legacy MBR sector read at offset {offset} returned {output.Written} of {destination.Length} bytes.");
        }
    }
}