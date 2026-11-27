using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const int LegacyTableProbeBytes = 1024;
    private const int LegacyTableProbeTimeoutMs = 60000;

    /// <summary>
    /// Reads the partition table of one Legacy region, detecting whether the region prefixes its table with a
    /// MediaTek layout header and whether the table itself is a UEFI GPT or a classic MBR.
    /// <para>读取一个 Legacy 区域的分区表，并判别该区域是否在分区表之前放置联发科布局头，以及表本身是 UEFI GPT 还是
    /// 经典 MBR。</para>
    /// </summary>
    /// <remarks>
    /// The device exposes eight physical regions and only the user area normally carries a partition table; the two
    /// boot areas instead begin with a <c>BRLYT</c> layout header, so reading a table from one of them is reported as a
    /// detected header with an unrecognised table rather than as a parse failure. Detection reads two sectors from the
    /// region start, which is enough to see both the prefixed header and a GPT header at LBA 1; when the header
    /// declares a table further in, a second probe is taken at that offset instead of the whole prefix being buffered.
    /// The chosen reader then performs its own validated reads. Nothing is written, guessed or repaired.
    /// <para>设备暴露八个物理区域，通常只有用户区承载分区表；两块引导区则以 <c>BRLYT</c> 布局头开始，因此从其中读取
    /// 分区表会被如实报告为“已识别头部、表未识别”，而不是解析失败。检测从区域起始处读取两个扇区，足以同时看到前置头部与
    /// LBA 1 上的 GPT 头部；当头部声明的表位置更靠后时，改为在该偏移处重新探测，而不是缓冲整个前缀。随后由选定的读取器
    /// 执行各自带校验的读取。不写入、不猜测、不修复。</para>
    /// </remarks>
    /// <param name="partitionId">Explicit discovered Legacy physical region, not a GPT entry index.
    /// <para>显式指定的已发现 Legacy 物理区域，不是 GPT 条目索引。</para></param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange.
    /// <para>取消信号会传播到每一次存储交换。</para></param>
    /// <returns>The observation, including the resolved table offset, the detected layout header, and either the GPT or the MBR payload.
    /// <para>该观测，含已解析的表偏移、检测到的布局头，以及 GPT 或 MBR 负载之一。</para></returns>
    /// <exception cref="InvalidOperationException">Storage is not ready, has not been discovered, or <paramref name="partitionId"/> is not a discovered region.
    /// <para>存储未就绪、尚未发现，或 <paramref name="partitionId"/> 不是已发现的区域。</para></exception>
    /// <exception cref="InvalidDataException">No recognised partition table was found in the region.
    /// <para>区域内未找到可识别的分区表。</para></exception>
    /// <exception cref="NotSupportedException">NOR/NAND page geometry does not define a block-device layout.
    /// <para>NOR/NAND 页几何信息未定义块设备布局。</para></exception>
    /// <exception cref="MtkProtocolException">A storage exchange faulted the session.
    /// <para>某次存储交换使会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkLegacyPartitionTableSnapshot> ReadLegacyPartitionTableAsync(byte partitionId,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLegacyPartitionTableOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<MtkLegacyPartitionTableSnapshot> ReadLegacyPartitionTableOwnedAsync(byte partitionId,
        CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.LegacyStorageReady);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = LegacyStorage ?? throw new InvalidOperationException("Legacy storage has not been discovered.");
        if (storage.Kind is MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException("Raw flash page geometry does not define a GPT or MBR block-device layout.");
        }

        var region = storage.Regions.Single(candidate => candidate.PartitionId == partitionId);
        storage.ValidateRange(partitionId, 0, LegacyTableProbeBytes);
        var probe = new byte[LegacyTableProbeBytes];
        await ReadLegacyProbeOwnedAsync(partitionId, 0, probe, cancellationToken).ConfigureAwait(false);

        var layoutHeader = MtkBromLayoutHeader.TryParse(probe, out var header) ? header : MtkBromLayoutHeader.None;
        var tableOffset = layoutHeader.IsRecognized ? layoutHeader.HeaderSize : 0L;
        if (layoutHeader.IsRecognized)
        {
            if (tableOffset <= 0 || tableOffset + LegacyTableProbeBytes > region.CapacityBytes)
            {
                throw new InvalidDataException(
                    "The MediaTek layout header at region " + MtkLegacyEmmcRegion.Describe(partitionId) +
                    " declares a header size of " + layoutHeader.HeaderSize +
                    " bytes, which leaves no room for a partition table.");
            }

            if (tableOffset >= probe.Length)
            {
                probe = new byte[LegacyTableProbeBytes];
                await ReadLegacyProbeOwnedAsync(partitionId, tableOffset, probe, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                probe = probe.AsSpan((int)tableOffset).ToArray();
            }
        }

        if (!PartitionTableProbe.TryDetect(probe, out var kind))
        {
            throw new InvalidDataException(BuildUnrecognizedTableMessage(partitionId, layoutHeader, tableOffset));
        }

        switch (kind)
        {
            case PartitionTableKind.Gpt:
                {
                    var gpt = await ReadLegacyGptAtOwnedAsync(partitionId, region, tableOffset, cancellationToken)
                        .ConfigureAwait(false);
                    ThrowIfDisposed();
                    return new MtkLegacyPartitionTableSnapshot(storage, partitionId, tableOffset, layoutHeader,
                        PartitionTableKind.Gpt, gpt, null);
                }

            case PartitionTableKind.Mbr:
                {
                    var mbr = await ReadLegacyMbrOwnedAsync(partitionId, tableOffset, cancellationToken)
                        .ConfigureAwait(false);
                    ThrowIfDisposed();
                    return new MtkLegacyPartitionTableSnapshot(storage, partitionId, tableOffset, layoutHeader,
                        PartitionTableKind.Mbr, null, mbr);
                }

            default:
                throw new InvalidDataException(BuildUnrecognizedTableMessage(partitionId, layoutHeader, tableOffset));
        }
    }

    private async Task<GptTable> ReadLegacyGptAtOwnedAsync(byte partitionId, MtkLegacyStorageRegion region,
        long tableOffset, CancellationToken cancellationToken)
    {
        return await GptReader.ReadAsync(ReadBlocksAsync, LegacyGptBlockSize,
            (ulong)((region.CapacityBytes - tableOffset) / LegacyGptBlockSize), cancellationToken)
            .ConfigureAwait(false);

        async ValueTask ReadBlocksAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            using var output = new UotanStudio.Comm.Protocols.Common.FixedBufferWriteStream(destination);
            await TransferLegacyStorageOwnedAsync(false, partitionId, checked(tableOffset + offset),
                destination.Length, output, LegacyGptReadTimeoutMs, null, token, nor: false).ConfigureAwait(false);
            if (output.Written != destination.Length)
            {
                throw new InvalidDataException("The Legacy GPT block read was incomplete.");
            }
        }
    }

    private async Task ReadLegacyProbeOwnedAsync(byte partitionId, long offset, byte[] destination,
        CancellationToken cancellationToken)
    {
        using var output = new UotanStudio.Comm.Protocols.Common.FixedBufferWriteStream(destination);
        await TransferLegacyStorageOwnedAsync(false, partitionId, offset, destination.Length, output,
            LegacyTableProbeTimeoutMs, null, cancellationToken, nor: false).ConfigureAwait(false);
        if (output.Written != destination.Length)
        {
            throw new InvalidDataException(
                "The Legacy partition-table probe at offset " + offset + " returned " + output.Written + " of " +
                destination.Length + " bytes.");
        }
    }

    private static string BuildUnrecognizedTableMessage(byte partitionId, MtkBromLayoutHeader layoutHeader,
        long tableOffset)
    {
        var region = MtkLegacyEmmcRegion.Describe(partitionId);
        if (layoutHeader.IsRecognized)
        {
            return "Region " + region + " starts with a MediaTek BRLYT layout header describing " +
                layoutHeader.DescribeMediaType() + " media of " + layoutHeader.TotalSize + " bytes, but the " +
                tableOffset + "-byte prefix is not followed by a recognised GPT or MBR partition table. Only the " +
                "user area (" + MtkLegacyEmmcRegion.User + ") normally carries one; boot areas hold boot images.";
        }

        return "Region " + region + " at byte offset " + tableOffset +
            " carries neither a GPT nor an MBR partition table.";
    }
}