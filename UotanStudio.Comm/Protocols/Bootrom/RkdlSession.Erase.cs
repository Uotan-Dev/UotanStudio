using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Reports whether READ_FLASH_ID identifies an eMMC medium, which is how
    /// <c>CRKDevice::GetFlashInfo</c> chooses the whole-media erase strategy.
    /// <para>报告 READ_FLASH_ID 是否标识 eMMC 介质；<c>CRKDevice::GetFlashInfo</c> 正是据此选择整介质擦除策略。</para>
    /// </summary>
    /// <returns><c>true</c> when the first four flash-ID bytes equal
    /// <see cref="RkdlConstants.EmmcFlashId"/>. <para>flash ID 前四字节等于
    /// <see cref="RkdlConstants.EmmcFlashId"/> 时返回 <c>true</c>。</para></returns>
    /// <exception cref="RkdlException">The READ_FLASH_ID transaction failed.
    /// <para>READ_FLASH_ID 事务失败。</para></exception>
    public bool IsEmmcFlash()
    {
        ThrowIfDisposed();
        var flashId = ReadFlashId();
        return flashId.Length >= sizeof(uint)
            && BinaryPrimitives.ReadUInt32LittleEndian(flashId) == RkdlConstants.EmmcFlashId;
    }

    /// <summary>
    /// Erases a sector range through ERASE_LBA transactions chunked at
    /// <see cref="RkdlConstants.EraseSectorChunk"/> sectors.
    /// <para>通过按 <see cref="RkdlConstants.EraseSectorChunk"/> 扇区分块的 ERASE_LBA 事务擦除扇区范围。</para>
    /// </summary>
    /// <param name="address">The first sector to erase. <para>要擦除的首个扇区。</para></param>
    /// <param name="sectorCount">The number of 512-byte sectors to erase. <para>要擦除的 512 字节扇区数。</para></param>
    /// <param name="progress">Optional receiver of the erased sector count. <para>可选的已擦除扇区数接收器。</para></param>
    /// <param name="cancellationToken">Checked before each chunk. <para>每个分块前检查。</para></param>
    /// <exception cref="RkdlException">An argument is negative, the range leaves the device geometry,
    /// or an ERASE_LBA transaction failed. <para>参数为负、范围超出设备几何，或某条 ERASE_LBA 事务失败。</para></exception>
    public void EraseLbaRange(long address, long sectorCount, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (address < 0 || sectorCount < 0)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Negative erase range: address = {address}, count = {sectorCount}.");
        }

        ValidateLbaRange(address, sectorCount);
        var erased = 0L;
        while (erased < sectorCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(sectorCount - erased, RkdlConstants.EraseSectorChunk);
            EraseLBA(address + erased, chunk);
            erased += chunk;
            progress?.Report(erased);
        }
    }

    /// <summary>
    /// Erases the whole reported media the way <c>CRKDevice::EraseAllBlocks</c> does: ERASE_LBA over
    /// the reported sector count when the medium is eMMC or the device advertises direct LBA, and
    /// ERASE_FORCE over every populated flash chip's blocks otherwise.
    /// <para>按 <c>CRKDevice::EraseAllBlocks</c> 的方式擦除整个上报介质：介质为 eMMC 或设备提供直接 LBA 时，
    /// 对上报扇区数执行 ERASE_LBA；否则对每个在位 flash 芯片的块执行 ERASE_FORCE。</para>
    /// </summary>
    /// <param name="forceBlockErase"><c>true</c> to skip the LBA path even on eMMC or direct-LBA
    /// devices, erasing per block instead. <para>为 <c>true</c> 时即使在 eMMC 或直接 LBA 设备上也跳过
    /// LBA 路径，改为逐块擦除。</para></param>
    /// <param name="progress">Optional receiver of the erased unit count (sectors on the LBA path,
    /// blocks on the block path). <para>可选的已擦除单位数接收器（LBA 路径为扇区，块路径为块）。</para></param>
    /// <param name="cancellationToken">Checked before each chunk. <para>每个分块前检查。</para></param>
    /// <returns>The number of blocks inside chunks the device answered with CSW status 1; always zero
    /// on the LBA path. <para>设备以 CSW 状态 1 应答的分块所含块数；LBA 路径恒为零。</para></returns>
    /// <exception cref="RkdlException">The flash geometry or capability query failed, the reported
    /// block geometry is unusable, or an erase transaction failed.
    /// <para>flash 几何或能力查询失败、上报的块几何不可用，或某条擦除事务失败。</para></exception>
    public int EraseAllFlash(bool forceBlockErase = false, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var geometry = RequireFlashGeometry();
        var capability = ReadCapability();

        if (!forceBlockErase && (IsEmmcFlash() || capability.DirectLba))
        {
            EraseLbaRange(0, geometry.SectorCount, progress, cancellationToken);
            return 0;
        }

        var blockCount = geometry.BlockCount;
        if (blockCount <= 0 || blockCount > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.FlashInfoFailed,
                $"READ_FLASH_INFO reported an unusable block geometry ({blockCount} blocks of {geometry.BlockSizeSectors} sectors), so a block erase cannot be addressed.");
        }

        IReadOnlyList<int> chips = geometry.FlashChips.Count > 0 ? geometry.FlashChips : [0];
        var badBlocks = 0;
        var erased = 0L;
        foreach (var chip in chips)
        {
            long block = 0;
            while (block < blockCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = (uint)Math.Min(blockCount - block, RkdlConstants.MaxEraseBlocksPerCommand);
                badBlocks += EraseBlocks((uint)block, chunk, force: true, lun: 0, ucFlashCs: (byte)chip);
                block += chunk;
                erased += chunk;
                progress?.Report(erased);
            }
        }

        return badBlocks;
    }
}