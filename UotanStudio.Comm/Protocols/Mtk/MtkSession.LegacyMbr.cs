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
    /// <see cref="InvalidDataException"/>.
    /// <para>每次只向设备请求一个扇区，因此缺失或损坏的记录会在产生它的那一跳上报，而不是静默截断列表。每个区间都
    /// 会按区域容量校验，链同时受跳数与已访问集合约束，损坏的环无法让读取器空转。不做修复、猜测或重放：无效的表以
    /// <see cref="InvalidDataException"/> 呈现。</para>
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
            return await ReadLegacyMbrOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<MtkMbrSnapshot> ReadLegacyMbrOwnedAsync(byte partitionId, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.LegacyStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = LegacyStorage ?? throw new InvalidOperationException("Legacy storage has not been discovered.");
        if (storage.Kind is MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException("Raw flash page geometry does not define a block-device layout.");
        }

        storage.ValidateRange(partitionId, 0, MbrSectorBytes);
        var region = storage.Regions.Single(candidate => candidate.PartitionId == partitionId);
        var totalSectors = checked((uint)(region.CapacityBytes / MbrSectorBytes));
        if (totalSectors == 0)
        {
            throw new InvalidDataException("The selected Legacy region reports no addressable sectors.");
        }

        var sector = new byte[MbrSectorBytes];
        await ReadSectorOwnedAsync(partitionId, 0, sector, cancellationToken).ConfigureAwait(false);
        if (!MtkMbrSnapshot.HasBootSignature(sector))
        {
            throw new InvalidDataException(
                "The Legacy region does not start with an MBR: the 0x55AA boot signature is absent from LBA 0.");
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

                await ReadSectorOwnedAsync(partitionId, (long)next * MbrSectorBytes, sector, cancellationToken)
                    .ConfigureAwait(false);
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
                            throw new InvalidDataException($"The extended boot record at LBA {next} declares a null link.");
                        }

                        next = following;
                        continue;
                    }

                    var absolute = checked(next + entry.FirstLba);
                    if (entry.SectorCount == 0)
                    {
                        throw new InvalidDataException(
                            $"The logical partition at LBA {absolute} in the record at LBA {next} declares no sectors.");
                    }

                    if (absolute >= totalSectors || entry.SectorCount > totalSectors - absolute)
                    {
                        throw new InvalidDataException(
                            $"The logical partition at LBA {absolute} with {entry.SectorCount} sectors exceeds the region.");
                    }

                    logical.Add(new MtkMbrLogicalPartition(logical.Count, entry.Status, entry.PartitionType,
                        absolute, entry.SectorCount, next));
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

            var first = extendedBase == 0 ? entry.FirstLba : checked(extendedBase + entry.FirstLba);
            if (entry.SectorCount == 0)
            {
                throw new InvalidDataException($"{table} declares a partition at LBA {first} with no sectors.");
            }

            if (first >= totalSectors || entry.SectorCount > totalSectors - first)
            {
                throw new InvalidDataException(
                    $"{table} declares a partition at LBA {first} with {entry.SectorCount} sectors, beyond the region.");
            }
        }
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