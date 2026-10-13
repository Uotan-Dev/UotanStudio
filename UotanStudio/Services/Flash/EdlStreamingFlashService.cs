using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Runs named-partition EDL program, backup and partition-table reads against the Streaming DLOAD backend,
/// which a command-mode target reaches without a Firehose programmer.
/// <para>面向流式 DLOAD 后端的 EDL 分区编程、回读与分区表读取；命令模式目标在没有 Firehose programmer
/// 时走这一后端。</para>
/// </summary>
internal static class EdlStreamingFlashService
{
    /// <summary>
    /// The block ceiling for one streaming bad-block scan, bounding the controller round-trips a single scan
    /// may cause.
    /// <para>单次流式坏块扫描的块数上限，为一次扫描可能产生的控制器往返数设界。</para>
    /// </summary>
    private const int MaxBadBlockScanBlocks = 512;

    /// <summary>
    /// The accepted codewords-per-page ceiling, derived from <c>sectors_per_page = PAGESIZE / sectorsize</c>
    /// over the supported sector-size range.
    /// <para>可接受的每页码字数上限，由 <c>sectors_per_page = PAGESIZE / sectorsize</c> 在受支持的扇区尺寸
    /// 区间上导出。</para>
    /// </summary>
    private const uint MaxSectorsPerPage = (uint)(StreamingConstants.MaxReportedSectorBytes
        / StreamingConstants.MinReportedSectorBytes);

    /// <summary>
    /// The largest bad-marker byte position that fits, namely the 10-bit BAD_BLOCK_BYTE_NUM field of
    /// NAND_DEV0_CFG1.
    /// <para>坏块标记字节位置可写入的最大值，即 NAND_DEV0_CFG1 中 BAD_BLOCK_BYTE_NUM 的 10 位域宽。</para>
    /// </summary>
    private const uint MaxBadBlockByteNumber = 0x3FF;

    /// <summary>
    /// The window that must stay addressable from the controller base: NAND_FLASH_BUFFER sits at base +0x100
    /// and carries one <see cref="StreamingConstants.BadMarkBufferSize"/>-byte bad-mark buffer.
    /// <para>从控制器基地址起需要保持可寻址的窗口：NAND_FLASH_BUFFER 位于基地址 +0x100，其上一个坏标记
    /// 缓冲区宽 <see cref="StreamingConstants.BadMarkBufferSize"/> 字节。</para>
    /// </summary>
    private const int FlashBufferWindowBytes = 0x100 + StreamingConstants.BadMarkBufferSize;

    /// <summary>
    /// Programs an image file into the start address the streaming backend reports for the named partition,
    /// confirming the whole source length through the target.
    /// <para>把镜像按分区名编程到流式后端上报的起始地址；写满源文件长度后由目标侧确认收束。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry to program. <para>要编程的设备分区条目。</para></param>
    /// <param name="imagePath">The image file to program. <para>要编程的镜像文件路径。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The programming result. <para>编程结果。</para></returns>
    internal static async Task<FlashResult> FlashAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        ArgumentNullException.ThrowIfNull(partition);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath} 镜像文件不存在。", imagePath);
        }

        var length = new FileInfo(imagePath).Length;
        progress?.Report(new FlashProgress(partition.Name, 0,
            $"Programming {partition.Name} through the Streaming DLOAD backend at 0x{partition.StartLba:X8}... " +
            $"正在经流式 DLOAD 后端编程 {partition.Name}..."));
        await using (var source = File.OpenRead(imagePath))
        {
            await streaming.WriteFlashAsync(partition.StartLba, partition.Name, source, length,
                ToChunkProgress(progress, partition.Name), confirmWrite: true, ct).ConfigureAwait(false);
        }

        progress?.Report(new FlashProgress(partition.Name, 100, "Stream flash complete. 流式编程完成。"));
        return new FlashResult
        {
            Success = true,
            Summary = $"EDL partition '{partition.Name}' programmed through the Streaming DLOAD backend " +
                $"(start 0x{partition.StartLba:X8}, {length} bytes). 已经流式 DLOAD 后端写入分区 '{partition.Name}'。"
        };
    }

    /// <summary>
    /// Reads the partition back in bounded chunks and publishes it atomically to the destination, replacing any
    /// existing content only once the staged length equals the requested length.
    /// <para>把分区按有界分块读回并原子发布到目标文件；暂存长度与请求长度一致时才替换既有内容。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry to read back. <para>要回读的设备分区条目。</para></param>
    /// <param name="outPath">The destination file path. <para>目标文件路径。</para></param>
    /// <param name="progress">Optional progress reporter. <para>可选的进度报告器。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The backup result. <para>备份结果。</para></returns>
    internal static async Task<BackupResult> BackupAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, string outPath, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);

        var destination = Path.GetFullPath(outPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The backup destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The backup destination directory '{directory}' does not exist.");
        }

        var sectorBytes = RequireReportedSectorBytes(streaming);
        var totalBytes = checked((long)partition.Sectors * sectorBytes);
        if (checked((long)partition.StartLba + totalBytes) > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"The reported {sectorBytes}-byte sector size scales partition '{partition.Name}' to {totalBytes} " +
                $"bytes at 0x{partition.StartLba:X8}, which leaves the 32-bit DIAG address space; the read was " +
                $"refused before any byte reached the wire. 按上报的 {sectorBytes} 字节扇区尺寸，分区 " +
                $"'{partition.Name}' 在 0x{partition.StartLba:X8} 处会展开为 {totalBytes} 字节，已超出 32 位 " +
                "DIAG 地址空间，因此在任何字节上线前拒绝读取。");
        }

        ct.ThrowIfCancellationRequested();
        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        long captured = 0;
        progress?.Report(new BackupProgress(partition.Name, 0,
            $"Reading {partition.Name} ({totalBytes} bytes) through the Streaming DLOAD backend... " +
            $"正在经流式 DLOAD 后端读取 {partition.Name}..."));
        try
        {
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var offset = 0L; offset < totalBytes; offset += StreamingConstants.MaxMemoryReadBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = (int)Math.Min(StreamingConstants.MaxMemoryReadBytes, totalBytes - offset);
                    var address = checked((uint)(partition.StartLba + offset));
                    var data = await streaming.ReadMemoryAsync(address, chunk, StreamingMemoryLinkMode.Auto, ct)
                        .ConfigureAwait(false);
                    await staging.WriteAsync(data, ct).ConfigureAwait(false);
                    captured = checked(captured + data.Length);
                    var percent = totalBytes == 0 ? 100 : (int)Math.Clamp(captured * 99 / totalBytes, 0, 99);
                    progress?.Report(new BackupProgress(partition.Name, percent,
                        $"Captured {captured} of {totalBytes} bytes... 已捕获 {captured}/{totalBytes} 字节..."));
                }

                if (staging.Length != totalBytes)
                {
                    throw new IOException(
                        $"The staged streaming backup holds {staging.Length} bytes after a reported {captured} but " +
                        $"{totalBytes} were requested, so the destination was left untouched.");
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            DiscardStaging(stagingPath);
        }

        progress?.Report(new BackupProgress(partition.Name, 100, "Streaming backup complete. 流式备份完成。"));
        return new BackupResult
        {
            Success = true,
            Summary = $"Backed up partition '{partition.Name}' ({captured} bytes) to '{destination}' through the " +
                "Streaming DLOAD backend. 已经流式 DLOAD 后端备份该分区。"
        };
    }

    /// <summary>
    /// Reads the device's own streaming partition table and parses it into entries through the reference record
    /// format.
    /// <para>读取设备自身的流式分区表并按参考记录格式解析为条目列表。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="tableAddress">The DIAG address the table is read from. <para>读取分区表的 DIAG 地址。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The parsed partition entries. <para>解析出的分区条目列表。</para></returns>
    internal static async Task<IReadOnlyList<StreamingPartitionEntry>> ReadPartitionTableAsync(
        StreamingProtocol streaming, uint tableAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        var bytes = await streaming.ReadMemoryAsync(tableAddress, StreamingConstants.PartitionTableBytes,
            StreamingMemoryLinkMode.Auto, ct).ConfigureAwait(false);
        return StreamingPartitionTable.Parse(bytes).Entries;
    }

    /// <summary>
    /// Picks the streaming partition entry whose name matches exactly, returning <see langword="null"/> when
    /// no entry matches.
    /// <para>按名字精确挑选流式分区条目，没有条目匹配时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="entries">The parsed partition entries. <para>已解析的分区条目。</para></param>
    /// <param name="partitionName">The name to match. <para>要匹配的分区名。</para></param>
    /// <returns>The matching entry, or <see langword="null"/>. <para>匹配的条目，或
    /// <see langword="null"/>。</para></returns>
    internal static StreamingPartitionEntry? ResolvePartition(IReadOnlyList<StreamingPartitionEntry> entries,
        string partitionName) =>
        entries.FirstOrDefault(entry => string.Equals(entry.Name, partitionName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates that the session really sits on the Streaming DLOAD backend and hands back its streaming
    /// protocol, refusing before any byte reaches the wire when it does not.
    /// <para>校验会话确实落在 Streaming DLOAD 后端上并交出它的流式协议实例；不在该后端时在任何字节上线之前
    /// 拒绝。</para>
    /// </summary>
    /// <param name="session">The initialized EDL session. <para>已初始化的 EDL 会话。</para></param>
    /// <returns>The session's streaming protocol instance. <para>会话的流式协议实例。</para></returns>
    /// <exception cref="InvalidOperationException">The session backend, protocol state or streaming instance
    /// is not the Streaming DLOAD combination. <para>会话后端、协议状态或流式实例并非 Streaming DLOAD
    /// 组合。</para></exception>
    internal static StreamingProtocol RequireStreamingSession(EdlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Backend != EdlDownloadBackend.StreamDload
            || session.ProtocolState != EdlProtocolState.Streaming
            || session.Streaming is not { } streaming)
        {
            throw new InvalidOperationException(
                $"The EDL session reports backend {session.Backend} in protocol state {session.ProtocolState}, " +
                $"which is not a Streaming DLOAD session, so no streaming command is sent. EDL 会话上报后端 " +
                $"{session.Backend}、协议状态 {session.ProtocolState}，并非 Streaming DLOAD 会话，因此不会" +
                "发送任何流式命令。");
        }

        return streaming;
    }

    /// <summary>
    /// Reads the bad-marker state of the first <paramref name="blockCount"/> erase blocks inside the device's
    /// own partition extent, issuing only the <c>check_block</c> page-read sequence.
    /// <para>读取设备自身分区范围内前 <paramref name="blockCount"/> 个擦除块的坏块标记状态，只发出
    /// <c>check_block</c> 的页读序列。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry supplying the capacity window. <para>提供容量范围的
    /// 设备分区条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="blockCount">The number of blocks to inspect. <para>要检查的块数。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The per-block verdicts plus a renderable summary. <para>逐块结论与可读摘要。</para></returns>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange, has no
    /// usable sector size, or its extent holds no erase block. <para>会话从未完成 hello、没有可用的扇区尺寸，
    /// 或其范围容不下一个擦除块。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A layout field or the requested block count is out of
    /// range. <para>布局字段或请求的块数越界。</para></exception>
    internal static async Task<EdlStreamingBadBlockMapResult> ReadBadBlockMapAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, EdlStreamingNandLayout layout, int blockCount, CancellationToken ct)
    {
        var sectorBytes = RequireStreamedGeometry(streaming);
        ValidateNandLayout(layout);
        var addressableBlocks = RequireAddressableBlocks(partition, layout, sectorBytes);
        if (blockCount is <= 0 || blockCount > addressableBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount),
                $"The device-reported extent of partition '{partition.Name}' holds {addressableBlocks} erase " +
                $"blocks, so {blockCount} blocks cannot be inspected. 设备上报的 '{partition.Name}' 范围只能" +
                $"容纳 {addressableBlocks} 个擦除块，因此无法检查 {blockCount} 个块。");
        }

        if (blockCount > MaxBadBlockScanBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount),
                $"A single streaming bad-block scan is bounded at {MaxBadBlockScanBlocks} blocks; the request " +
                $"asked for {blockCount}. 单次流式坏块扫描以 {MaxBadBlockScanBlocks} 个块为上限，本次请求了 " +
                $"{blockCount} 个。");
        }

        var table = CreateTable(streaming, layout);
        var bad = new List<uint>();
        for (uint block = 0; block < blockCount; block++)
        {
            ct.ThrowIfCancellationRequested();
            if (await table.CheckBlockAsync(block, ct).ConfigureAwait(false))
            {
                bad.Add(block);
            }
        }

        return new EdlStreamingBadBlockMapResult(blockCount, bad, table.Policy,
            $"Inspected {blockCount} erase block(s) of '{partition.Name}' through the streaming NAND controller " +
            $"under the {table.Policy} policy; {bad.Count} carry a bad marker. 已按 {table.Policy} 策略经流式 " +
            $"NAND 控制器检查 '{partition.Name}' 的 {blockCount} 个擦除块；其中 {bad.Count} 个带坏块标记。");
    }

    /// <summary>
    /// Writes a bad marker into a currently good block through the <c>mark_bad</c> sequence; this is
    /// irreversible, and an unconfirmed request is rejected by the Comm layer before the first byte.
    /// <para>经 <c>mark_bad</c> 序列给当前完好的块写入坏块标记；这是不可逆写入，未确认时由 Comm 层在首个
    /// 字节之前拒绝。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry supplying the capacity
    /// window. <para>提供容量范围的设备分区条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to mark. <para>要标记的块号。</para></param>
    /// <param name="confirm">Set to <see langword="true"/> to confirm the marker
    /// write. <para>置为 <see langword="true"/> 以确认写入标记。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The mark outcome plus a renderable summary. <para>标记结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="confirm"/> is
    /// <see langword="false"/>. <para>确认标志为 <see langword="false"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block leaves the device-reported extent, or a layout
    /// field is out of range. <para>块号超出设备上报范围，或布局字段越界。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in streaming mode, lacks a usable
    /// geometry, or runs the peek-only stock Qualcomm loader. <para>会话不在流式模式、缺少可用几何信息，或运行
    /// 仅支持 peek 的原版 Qualcomm loader。</para></exception>
    internal static async Task<EdlStreamingBadBlockWriteResult> MarkBlockBadAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, EdlStreamingNandLayout layout, uint block, bool confirm,
        CancellationToken ct)
    {
        var newlyApplied = await ApplyBadMarkerAsync(streaming, partition, layout, block, confirm, marking: true, ct)
            .ConfigureAwait(false);
        return new EdlStreamingBadBlockWriteResult(block, newlyApplied, Marking: true,
            newlyApplied
                ? $"Block {block} in the '{partition.Name}' extent now carries a bad marker. " +
                    $"'{partition.Name}' 范围内的块 {block} 已写入坏块标记。"
                : $"Block {block} in the '{partition.Name}' extent already carried a bad marker, so nothing was " +
                    $"programmed. '{partition.Name}' 范围内的块 {block} 早已带坏块标记，因此未编程任何内容。");
    }

    /// <summary>
    /// Erases the whole block to clear a bad marker; the erase destroys every page in the block, and an
    /// unconfirmed request is rejected by the Comm layer before the first byte.
    /// <para>擦除整块以解除坏块标记；擦除会毁掉该块内的全部页，未确认时由 Comm 层在首个字节之前拒绝。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry supplying the capacity
    /// window. <para>提供容量范围的设备分区条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to erase. <para>要擦除的块号。</para></param>
    /// <param name="confirm">Set to <see langword="true"/> to confirm the
    /// erase. <para>置为 <see langword="true"/> 以确认擦除。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The un-mark outcome plus a renderable summary. <para>解除标记结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="confirm"/> is
    /// <see langword="false"/>. <para>确认标志为 <see langword="false"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block leaves the device-reported extent, or a layout
    /// field is out of range. <para>块号超出设备上报范围，或布局字段越界。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in streaming mode, lacks a usable
    /// geometry, or runs the peek-only stock Qualcomm loader. <para>会话不在流式模式、缺少可用几何信息，或运行
    /// 仅支持 peek 的原版 Qualcomm loader。</para></exception>
    internal static async Task<EdlStreamingBadBlockWriteResult> UnmarkBlockBadAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, EdlStreamingNandLayout layout, uint block, bool confirm,
        CancellationToken ct)
    {
        var newlyApplied = await ApplyBadMarkerAsync(streaming, partition, layout, block, confirm, marking: false, ct)
            .ConfigureAwait(false);
        return new EdlStreamingBadBlockWriteResult(block, newlyApplied, Marking: false,
            newlyApplied
                ? $"Block {block} in the '{partition.Name}' extent was erased, discarding every page it held, and " +
                    $"its bad marker is gone. '{partition.Name}' 范围内的块 {block} 已被擦除，其全部页随之丢失，" +
                    "坏块标记已解除。"
                : $"Block {block} in the '{partition.Name}' extent carried no bad marker, so nothing was erased. " +
                    $"'{partition.Name}' 范围内的块 {block} 没有坏块标记，因此未擦除任何内容。");
    }

    /// <summary>
    /// Reads only the three NAND_DEV0 configuration words and reports the decoded fields and candidate
    /// values; no register is written.
    /// <para>只读取三个 NAND_DEV0 配置字并报告解码字段与候选值；不写出任何寄存器。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The register snapshot, decoded fields and candidate values. <para>寄存器快照、解码字段与候选
    /// 值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">A layout field is out of range. <para>布局字段越界。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">The session is not in streaming mode or never reported a
    /// usable geometry. <para>会话不在流式模式或未上报可用几何信息。</para></exception>
    internal static async Task<EdlStreamingNandRegisterResult> ReadNandRegisterReportAsync(StreamingProtocol streaming,
        EdlStreamingNandLayout layout, CancellationToken ct)
    {
        var sectorBytes = RequireStreamedGeometry(streaming);
        ValidateNandLayout(layout);
        var table = CreateTable(streaming, layout);
        var snapshot = await table.ReadRegisterSnapshotAsync(ct).ConfigureAwait(false);
        var userDataSize = (uint)sectorBytes;
        var usesBch = StreamingNandRegisters.UsesBchEcc(snapshot.Dev0Cfg1);
        var decodedUserData = StreamingNandRegisters.GetUdSize(snapshot.Dev0Cfg0);
        var candidateCfg0 = StreamingNandRegisters.ApplyUdSize(snapshot.Dev0Cfg0, userDataSize);
        var candidateEccCfg =
            StreamingNandRegisters.ApplyUdSizeToEccCfg(snapshot.Dev0Cfg1, snapshot.Dev0EccCfg, userDataSize);
        var bchPulseValue = StreamingNandRegisters.BchResetPulseValue(snapshot.Dev0EccCfg);
        return new EdlStreamingNandRegisterResult(
            snapshot.Dev0Cfg0,
            snapshot.Dev0Cfg1,
            snapshot.Dev0EccCfg,
            decodedUserData,
            usesBch,
            candidateCfg0,
            candidateEccCfg,
            bchPulseValue,
            $"CFG0=0x{snapshot.Dev0Cfg0:X8} reads user-data {decodedUserData} with BCH ECC " +
                $"{(usesBch ? "enabled" : "disabled")}, CFG1=0x{snapshot.Dev0Cfg1:X8}, " +
                $"ECC_CFG=0x{snapshot.Dev0EccCfg:X8}. Applying set_udsize(0x{userDataSize:X4}) to the reported " +
                $"sector size would give CFG0=0x{candidateCfg0:X8} and ECC_CFG=0x{candidateEccCfg:X8}, and a " +
                $"bch_reset pulse would write ECC_CFG=0x{bchPulseValue:X8}; nothing was programmed. " +
                $"CFG0=0x{snapshot.Dev0Cfg0:X8} 的用户数据域读到 {decodedUserData}，BCH ECC " +
                $"{(usesBch ? "已使能" : "未使能")}，CFG1=0x{snapshot.Dev0Cfg1:X8}，" +
                $"ECC_CFG=0x{snapshot.Dev0EccCfg:X8}。把 set_udsize(0x{userDataSize:X4}) 套到上报的扇区尺寸上" +
                $"会得到 CFG0=0x{candidateCfg0:X8}、ECC_CFG=0x{candidateEccCfg:X8}；bch_reset 脉冲会写入 " +
                $"ECC_CFG=0x{bchPulseValue:X8}。未编程任何寄存器。");
    }

    /// <summary>
    /// Runs one controller page-read sequence over a block and reports the bad-block and ECC verdicts; the
    /// command registers are pulsed, so confirmation is required, but nothing is programmed.
    /// <para>对一个块执行一次控制器页读并回报坏块与 ECC 结论；会脉冲命令寄存器，因此必须确认，但绝不编程
    /// 任何内容。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry supplying the capacity
    /// window. <para>提供容量范围的设备分区条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to read. <para>要读取的块号。</para></param>
    /// <param name="confirm">Set to <see langword="true"/> to confirm the controller command
    /// sequence. <para>置为 <see langword="true"/> 以确认控制器命令序列。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The status words and decoded verdicts. <para>状态字与解码结论。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="confirm"/> is
    /// <see langword="false"/>. <para>确认标志为 <see langword="false"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block leaves the device-reported extent, or a layout
    /// field is out of range. <para>块号超出设备上报范围，或布局字段越界。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in streaming mode, lacks a usable
    /// geometry, or runs the peek-only stock Qualcomm loader. <para>会话不在流式模式、缺少可用几何信息，或运行
    /// 仅支持 peek 的原版 Qualcomm loader。</para></exception>
    internal static async Task<EdlStreamingNandEccResult> EvaluateNandCodewordAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, EdlStreamingNandLayout layout, uint block, bool confirm,
        CancellationToken ct)
    {
        var sectorBytes = RequireStreamedGeometry(streaming);
        ValidateNandLayout(layout);
        RequireWritableLoader(streaming);
        RequireBlockInRange(block, RequireAddressableBlocks(partition, layout, sectorBytes), partition, layout);
        var table = CreateTable(streaming, layout);
        var status = await table.ReadCodewordStatusAsync(block, confirm, ct).ConfigureAwait(false);
        var eccVerdict = status.EccCorrectedBits < 0
            ? "an uncorrectable codeword"
            : $"{status.EccCorrectedBits} corrected bit(s)";
        var chineseVerdict = status.EccCorrectedBits < 0
            ? "不可纠错的码字"
            : $"{status.EccCorrectedBits} 个纠正位";
        return new EdlStreamingNandEccResult(block, status.BufferStatus, status.FlashCommandValue,
            status.IsBadBlock, status.EccCorrectedBits,
            $"Block {block} of '{partition.Name}': NAND_BUFFER_STATUS=0x{status.BufferStatus:X8}, " +
                $"NAND_FLASH_CMD reads 0x{status.FlashCommandValue:X8}, the block is " +
                $"{(status.IsBadBlock ? "bad" : "good")} and check_ecc_status yields {eccVerdict}. " +
                "Only a page-read sequence ran; nothing was programmed or erased. " +
                $"块 {block}（分区 '{partition.Name}'）：NAND_BUFFER_STATUS=0x{status.BufferStatus:X8}，" +
                $"NAND_FLASH_CMD 读到 0x{status.FlashCommandValue:X8}，该块判定为" +
                $"{(status.IsBadBlock ? "坏" : "好")}，check_ecc_status 给出{chineseVerdict}。" +
                "只执行了页读序列；未编程也未擦除任何内容。");
    }

    /// <summary>
    /// Negotiates trusted or untrusted secure mode with DIAG 0x17; it alters no storage, so only the
    /// completed hello exchange is required.
    /// <para>用 DIAG 0x17 协商受信或不受信安全模式；不改动任何存储，因此只要求会话已经完成 hello。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="trusted"><see langword="true"/> selects the trusted mode byte
    /// 0x01. <para>为 <see langword="true"/> 时选择受信模式字节 0x01。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Whether the target acknowledged the negotiation. <para>目标是否确认协商结果。</para></returns>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange, so it is
    /// not in streaming mode. <para>会话从未完成 hello，因此不在流式模式。</para></exception>
    internal static async Task<EdlStreamingControlResult> EnterSecureModeAsync(StreamingProtocol streaming,
        bool trusted, CancellationToken ct)
    {
        _ = RequireStreamingMode(streaming);
        var acknowledged = await streaming.SecureModeAsync(trusted, ct).ConfigureAwait(false);
        return new EdlStreamingControlResult(acknowledged,
            acknowledged
                ? $"The streaming target acknowledged {(trusted ? "trusted" : "untrusted")} secure mode. " +
                    $"流式目标已确认{(trusted ? "受信" : "不受信")}安全模式。"
                : $"The streaming target did not acknowledge {(trusted ? "trusted" : "untrusted")} secure mode. " +
                    $"流式目标未确认{(trusted ? "受信" : "不受信")}安全模式。");
    }

    /// <summary>
    /// Runs the secure-mode negotiation plus the qclose exchange that puts the target into flash mode; an
    /// unconfirmed request is rejected by the Comm layer before the operation lease is taken.
    /// <para>执行安全模式协商加 qclose 收束，把目标切换到闪存模式；未确认时由 Comm 层在取得操作租约之前
    /// 拒绝。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="confirm">Set to <see langword="true"/> to confirm the flash-mode
    /// transition. <para>置为 <see langword="true"/> 以确认切换到闪存模式。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The transition result plus a renderable summary. <para>切换结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="confirm"/> is
    /// <see langword="false"/>. <para>确认标志为 <see langword="false"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange or never
    /// reported a usable geometry. <para>会话从未完成 hello，或未上报可用的几何信息。</para></exception>
    internal static async Task<EdlStreamingControlResult> EnterFlashModeAsync(StreamingProtocol streaming,
        bool confirm, CancellationToken ct)
    {
        _ = RequireStreamedGeometry(streaming);
        var secure = await streaming.EnterFlashModeAsync(confirm, ct).ConfigureAwait(false);
        return new EdlStreamingControlResult(secure,
            $"Streaming flash mode entered; the target {(secure ? "acknowledged" : "did not acknowledge")} the " +
                $"secure-mode negotiation and the preceding qclose was already attempted. 已进入流式闪存模式；" +
                $"目标{(secure ? "已确认" : "未确认")}安全模式协商，此前的 qclose 也已尝试。");
    }

    /// <summary>
    /// Sends the raw on-disk partition table with DIAG 0x19. The file must parse into at least one record, or
    /// the request is refused before any byte reaches the wire.
    /// <para>把磁盘上的原始分区表按 DIAG 0x19 发送。文件必须能解析出至少一条记录，否则在任何字节上线之前
    /// 拒绝。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="tablePath">The raw partition table file path. <para>原始分区表文件路径。</para></param>
    /// <param name="overrideExisting"><see langword="true"/> sends mode byte 0x01, letting the target override
    /// its existing table. <para>为 <see langword="true"/> 时发送模式字节 0x01，允许目标覆盖既有表。</para></param>
    /// <param name="confirm">Set to <see langword="true"/> to confirm this partition-table
    /// write. <para>置为 <see langword="true"/> 以确认这次分区表写入。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The target status byte plus a renderable summary. <para>目标状态字节与可读摘要。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="confirm"/> is
    /// <see langword="false"/>. <para>确认标志为 <see langword="false"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The table file does not exist. <para>表文件不存在。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in streaming mode, lacks a usable
    /// geometry, or the file is not a valid partition table. <para>会话不在流式模式、缺少可用几何信息，或文件
    /// 不是有效的分区表。</para></exception>
    internal static async Task<EdlStreamingPartitionTableResult> SendPartitionTableAsync(StreamingProtocol streaming,
        string tablePath, bool overrideExisting, bool confirm, CancellationToken ct)
    {
        _ = RequireStreamedGeometry(streaming);
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePath);
        var destination = Path.GetFullPath(tablePath);
        var (table, parsed) = await ReadValidatedPartitionTableAsync(destination, ct).ConfigureAwait(false);
        var accepted = await streaming.SendPartitionTableAsync(table, overrideExisting, confirm, ct)
            .ConfigureAwait(false);
        return new EdlStreamingPartitionTableResult(accepted, parsed.Entries.Count, overrideExisting,
            $"Sent the {table.Length}-byte streaming partition table with {parsed.Entries.Count} record(s) in " +
                $"{(overrideExisting ? "override" : "keep")} mode; the target {(accepted ? "accepted" : "refused")} " +
                $"it. 已按{(overrideExisting ? "覆盖" : "保留")}模式发送 {table.Length} 字节的流式分区表，" +
                $"含 {parsed.Entries.Count} 条记录；目标{(accepted ? "已接受" : "已拒绝")}。");
    }

    /// <summary>
    /// Finalizes the current stream section with qclose, the teardown a streaming write always needs. Only the
    /// completed hello exchange is required, so closing stays reachable.
    /// <para>用 qclose 收束当前流式段，是流式写入之后的必备拆除步骤。只要求会话已经完成 hello，因此收尾始终
    /// 可达。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Whether the close was acknowledged. <para>关闭是否被确认。</para></returns>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange.
    /// <para>会话从未完成 hello。</para></exception>
    internal static async Task<EdlStreamingControlResult> CloseStreamAsync(StreamingProtocol streaming,
        CancellationToken ct)
    {
        _ = RequireStreamingMode(streaming);
        var closed = await streaming.StreamCloseAsync(ct).ConfigureAwait(false);
        return new EdlStreamingControlResult(closed,
            closed
                ? "The streaming target acknowledged the closing qclose. 流式目标已确认收尾的 qclose。"
                : "The streaming target did not acknowledge the closing qclose; treat the session as unusable. " +
                    "流式目标未确认收尾的 qclose，应把该会话视为不可再用。");
    }

    /// <summary>
    /// The shared path for marking and un-marking: every validation first, then the Comm confirmation gate.
    /// <para>标记或解除标记的共用流程：先做全部校验，再交给 Comm 层的确认门。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The device partition entry supplying the capacity
    /// window. <para>提供容量范围的设备分区条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to mark or erase. <para>要标记或擦除的块号。</para></param>
    /// <param name="confirm">Set to <see langword="true"/> to confirm the marker write or the
    /// erase. <para>置为 <see langword="true"/> 以确认写入标记或擦除。</para></param>
    /// <param name="marking"><see langword="true"/> writes a marker, <see langword="false"/> clears
    /// one. <para>为 <see langword="true"/> 时写入标记，为 <see langword="false"/> 时解除标记。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Whether this call changed the block's marker state. <para>本次调用是否改变了该块的标记
    /// 状态。</para></returns>
    private static async Task<bool> ApplyBadMarkerAsync(StreamingProtocol streaming,
        StreamingPartitionEntry partition, EdlStreamingNandLayout layout, uint block, bool confirm, bool marking,
        CancellationToken ct)
    {
        var sectorBytes = RequireStreamedGeometry(streaming);
        ValidateNandLayout(layout);
        RequireWritableLoader(streaming);
        RequireBlockInRange(block, RequireAddressableBlocks(partition, layout, sectorBytes), partition, layout);
        var table = CreateTable(streaming, layout);
        return marking
            ? await table.MarkBadAsync(block, confirm, ct).ConfigureAwait(false)
            : await table.UnmarkBadAsync(block, confirm, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and validates the raw partition table on disk, refusing on any mismatch of length, magic words or
    /// record count.
    /// <para>读取并校验磁盘上的原始分区表：长度、魔数与记录数任一项不符就拒绝。</para>
    /// </summary>
    /// <param name="tablePath">The raw partition table file path. <para>原始分区表文件路径。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The table bytes together with the parsed table. <para>表字节与已解析的表。</para></returns>
    private static async Task<(byte[] Table, StreamingPartitionTable Parsed)> ReadValidatedPartitionTableAsync(
        string tablePath, CancellationToken ct)
    {
        if (!File.Exists(tablePath))
        {
            throw new FileNotFoundException($"Partition table file not found: {tablePath} 分区表文件不存在。", tablePath);
        }

        var declaredLength = new FileInfo(tablePath).Length;
        if (declaredLength != StreamingConstants.PartitionTableBytes)
        {
            throw new InvalidOperationException(
                $"The streaming partition table must be exactly {StreamingConstants.PartitionTableBytes} bytes " +
                $"but '{tablePath}' is {declaredLength}; nothing was sent. 流式分区表必须正好 " +
                $"{StreamingConstants.PartitionTableBytes} 字节，而 '{tablePath}' 是 {declaredLength} 字节；" +
                "未发送任何内容。");
        }

        var table = await File.ReadAllBytesAsync(tablePath, ct).ConfigureAwait(false);
        if (table.Length != StreamingConstants.PartitionTableBytes)
        {
            throw new InvalidOperationException(
                $"'{tablePath}' changed while it was being read: {table.Length} bytes for a table that must be " +
                $"{StreamingConstants.PartitionTableBytes}; nothing was sent. '{tablePath}' 在读取过程中发生变化：" +
                $"读到 {table.Length} 字节，而该表必须为 {StreamingConstants.PartitionTableBytes} 字节；未发送" +
                "任何内容。");
        }

        var parsed = StreamingPartitionTable.Parse(table);
        var recognized = (parsed.Magic1 == StreamingPartitionTable.LegacyMagic1
                && parsed.Magic2 == StreamingPartitionTable.LegacyMagic2)
            || (parsed.Magic1 == StreamingPartitionTable.Partition9kMagic1
                && parsed.Magic2 == StreamingPartitionTable.Partition9kMagic2);
        if (!recognized || parsed.Entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{tablePath}' is not a partition table the reference layouts recognize (magic " +
                    $"0x{parsed.Magic1:X8}/0x{parsed.Magic2:X8}, {parsed.Entries.Count} record(s)), so the " +
                    "irreversible table write was refused. 该文件的魔数 " +
                    $"0x{parsed.Magic1:X8}/0x{parsed.Magic2:X8} 与 {parsed.Entries.Count} 条记录不符合参考布局，" +
                    "因此已拒绝这一不可逆的分区表写入。");
        }

        return (table, parsed);
    }

    /// <summary>
    /// Requires the session to have completed the hello exchange, which is what puts it in streaming mode.
    /// <para>要求会话已经完成 hello 交换，也就是确实处在流式模式。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <returns>The device information the streaming hello reported. <para>流式 hello 上报的设备信息。</para></returns>
    private static StreamingDeviceInfo RequireStreamingMode(StreamingProtocol streaming)
    {
        ArgumentNullException.ThrowIfNull(streaming);
        if (streaming.Device is not { } device)
        {
            throw new InvalidOperationException(
                "The target never answered the streaming hello, so the session is not in streaming mode and no " +
                "command is sent. 目标从未应答流式 hello，因此会话不处于流式模式，不会发送任何命令。");
        }

        return device;
    }

    /// <summary>
    /// Requires a single plausible reported sector size on top of the hello exchange, because every block
    /// address and length is scaled by it.
    /// <para>在完成 hello 之外还要求目标上报了唯一合理的扇区尺寸；每个块地址与长度都要按它缩放。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <returns>The sector size the target reported. <para>目标上报的扇区尺寸。</para></returns>
    private static int RequireStreamedGeometry(StreamingProtocol streaming)
    {
        _ = RequireStreamingMode(streaming);
        return RequireReportedSectorBytes(streaming);
    }

    /// <summary>
    /// Refuses the request through the link mode the target itself reported, because the peek-only stock
    /// Qualcomm loader cannot run a NAND register sequence.
    /// <para>仅支持 peek 的原版 Qualcomm loader 无法执行 NAND 寄存器序列，因此按目标自己上报的链接模式
    /// 拒绝。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    private static void RequireWritableLoader(StreamingProtocol streaming)
    {
        if (streaming.NegotiatedLinkMode == StreamingMemoryLinkMode.Qualcomm)
        {
            throw new InvalidOperationException(
                "The streaming target reported the peek-only stock Qualcomm loader, so the NAND register and " +
                    "marker sequence was refused before any register write. 流式目标上报的是仅支持 peek 的原版 " +
                    "Qualcomm loader，因此在任何寄存器写入之前已拒绝这一 NAND 寄存器与标记序列。");
        }
    }

    private static StreamingBadBlockTable CreateTable(StreamingProtocol streaming, EdlStreamingNandLayout layout) =>
        new(streaming, layout.NandBase, layout.PagesPerBlock, layout.SectorsPerPage,
            layout.BadBlockByteNum, layout.BadBlockInSpareArea, StreamingBadBlockPolicy.Mark);

    /// <summary>
    /// Validates the controller layout against the field widths and keeps the NAND flash-buffer window inside
    /// the 32-bit DIAG address space.
    /// <para>按位域宽度校验控制器布局，并把 NAND 闪存缓冲区窗口限制在 32 位 DIAG 地址空间内。</para>
    /// </summary>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    private static void ValidateNandLayout(EdlStreamingNandLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.PagesPerBlock is 0 or > StreamingConstants.MaxReportedPagesPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(layout),
                $"The streaming NAND layout declares {layout.PagesPerBlock} pages per block, outside " +
                    $"1..{StreamingConstants.MaxReportedPagesPerBlock}. 流式 NAND 布局声明每块 " +
                    $"{layout.PagesPerBlock} 页，超出 1..{StreamingConstants.MaxReportedPagesPerBlock}。");
        }

        if (layout.SectorsPerPage is 0 or > MaxSectorsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(layout),
                $"The streaming NAND layout declares {layout.SectorsPerPage} codewords per page, outside " +
                    $"1..{MaxSectorsPerPage}. 流式 NAND 布局声明每页 {layout.SectorsPerPage} 个码字，超出 " +
                    $"1..{MaxSectorsPerPage}。");
        }

        if (layout.BadBlockByteNum > MaxBadBlockByteNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(layout),
                $"The bad-marker byte position {layout.BadBlockByteNum} does not fit the 10-bit CFG1 field. " +
                $"坏块标记字节位置 {layout.BadBlockByteNum} 放不进 CFG1 的 10 位域。");
        }

        if (layout.BadBlockInSpareArea > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(layout),
                $"The bad-marker in-spare-area flag must be 0 or 1, not {layout.BadBlockInSpareArea}. " +
                $"坏块标记位于 spare 区的标志必须是 0 或 1，而不是 {layout.BadBlockInSpareArea}。");
        }

        if (checked((long)layout.NandBase + FlashBufferWindowBytes) > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(layout),
                $"The NAND flash buffer at 0x{layout.NandBase:X8} plus {FlashBufferWindowBytes} bytes leaves the " +
                    $"32-bit DIAG address space. 0x{layout.NandBase:X8} 处的 NAND 闪存缓冲区加上 " +
                    $"{FlashBufferWindowBytes} 字节已超出 32 位 DIAG 地址空间。");
        }
    }

    /// <summary>
    /// Derives the addressable erase-block count from the target-reported partition length and the layout,
    /// refusing when the length cannot hold one whole block.
    /// <para>按目标上报的分区长度与布局算出可寻址的擦除块数；长度容不下一个整块时直接拒绝。</para>
    /// </summary>
    /// <param name="partition">The device partition entry supplying the extent. <para>提供范围的设备分区
    /// 条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="sectorBytes">The sector size the target reported. <para>目标上报的扇区尺寸。</para></param>
    /// <returns>The number of addressable erase blocks. <para>可寻址的擦除块数。</para></returns>
    private static uint RequireAddressableBlocks(StreamingPartitionEntry partition, EdlStreamingNandLayout layout,
        int sectorBytes)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(layout);
        var extentBytes = checked((long)partition.Sectors * sectorBytes);
        var blockBytes = checked((long)layout.PagesPerBlock * layout.SectorsPerPage * sectorBytes);
        if (extentBytes < blockBytes)
        {
            throw new InvalidOperationException(
                $"The device-reported {extentBytes}-byte extent of partition '{partition.Name}' does not hold a " +
                    $"single {blockBytes}-byte erase block, so no block can be addressed in it. 设备上报的分区 " +
                    $"'{partition.Name}' 只有 {extentBytes} 字节，容不下一个 {blockBytes} 字节的擦除块，因此" +
                    "无法在其中寻址任何块。");
        }

        var blocks = extentBytes / blockBytes;
        return blocks > uint.MaxValue ? uint.MaxValue : (uint)blocks;
    }

    /// <summary>
    /// Range-checks the block against both the reported block count and the uint32 ceiling of the linear page
    /// address, with checked arithmetic for both.
    /// <para>把块号对照上报块数与线性页地址的 uint32 上限双重校验；两项都用 checked 运算。</para>
    /// </summary>
    /// <param name="block">The block to address. <para>要寻址的块号。</para></param>
    /// <param name="addressableBlocks">The addressable erase-block count. <para>可寻址的擦除块数。</para></param>
    /// <param name="partition">The device partition entry supplying the extent. <para>提供范围的设备分区
    /// 条目。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    private static void RequireBlockInRange(uint block, uint addressableBlocks, StreamingPartitionEntry partition,
        EdlStreamingNandLayout layout)
    {
        if (block >= addressableBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(block),
                $"Block {block} leaves the {addressableBlocks} erase block(s) the device-reported extent of " +
                    $"partition '{partition.Name}' holds. 块 {block} 超出设备上报的分区 '{partition.Name}' 所" +
                    $"容纳的 {addressableBlocks} 个擦除块。");
        }

        if (checked((ulong)block * layout.PagesPerBlock) > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(block),
                $"The linear page address for block {block} with {layout.PagesPerBlock} pages per block leaves " +
                    $"the 32-bit controller address space. 块 {block} 在每块 {layout.PagesPerBlock} 页下的线性" +
                    "页地址已超出 32 位控制器地址空间。");
        }
    }

    /// <summary>
    /// Reads back the sector size the target itself reported, refusing the read when no usable geometry
    /// exists instead of falling back to a guessed 4096.
    /// <para>取回目标自己上报的扇区尺寸；没有可用几何信息时拒绝读取，绝不退回猜测的 4096。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <returns>The sector size the target reported. <para>目标上报的扇区尺寸。</para></returns>
    private static int RequireReportedSectorBytes(StreamingProtocol streaming)
    {
        if (streaming.Device is null || !streaming.Device.TryGetReportedSectorBytes(out var sectorBytes))
        {
            throw new InvalidOperationException(
                "The streaming target has not reported a single plausible sector size, so the partition " +
                "length cannot be scaled; no fallback size is assumed. 流式目标尚未上报唯一合理的扇区尺寸，" +
                "因此无法缩放分区长度；不会退回到任何猜测尺寸。");
        }

        return sectorBytes;
    }

    private static IProgress<long>? ToChunkProgress(IProgress<FlashProgress>? progress, string partitionName)
    {
        if (progress is null)
        {
            return null;
        }

        return new Progress<long>(captured =>
            progress.Report(new FlashProgress(partitionName, 50,
                $"Streamed {captured} bytes... 已流式写出 {captured} 字节...")));
    }

    private static void DiscardStaging(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The controller layout a streaming NAND operation runs with: base address, pages per block, codewords per
/// page, bad-marker byte position and whether that marker lives in the spare area.
/// <para>流式 NAND 操作使用的控制器布局：基地址、每块页数、每页码字数、坏块标记字节位置，以及标记是否位于
/// spare 区。</para>
/// </summary>
/// <param name="NandBase">The physical base address of the NAND controller
/// registers. <para>NAND 控制器寄存器的物理基地址。</para></param>
/// <param name="PagesPerBlock">The pages inside one erase block. <para>一个擦除块内的页数。</para></param>
/// <param name="SectorsPerPage">The codewords per page. <para>一页的码字数。</para></param>
/// <param name="BadBlockByteNum">The marker byte position written into CFG1. <para>写入 CFG1 的标记字节
/// 位置。</para></param>
/// <param name="BadBlockInSpareArea">1 when the marker lives in the spare area, else 0. <para>标记位于 spare
/// 区时为 1，否则为 0。</para></param>
internal sealed record EdlStreamingNandLayout(
    uint NandBase,
    uint PagesPerBlock,
    uint SectorsPerPage,
    uint BadBlockByteNum,
    uint BadBlockInSpareArea);

/// <summary>
/// The outcome of one streaming session-control exchange: whether the target acknowledged it, plus
/// renderable result text.
/// <para>一次流式会话控制交换的结果：目标是否确认，以及可直接显示的结果文本。</para>
/// </summary>
/// <param name="Success"><see langword="true"/> when the target acknowledged the negotiation or the
/// close. <para>目标已确认协商或收束时为 <see langword="true"/>。</para></param>
/// <param name="Summary">The result text. <para>结果文本。</para></param>
internal sealed record EdlStreamingControlResult(bool Success, string Summary);

/// <summary>
/// The outcome of one raw partition-table send: whether the target accepted the table, the record count it
/// carries, the mode byte that was used, and renderable text.
/// <para>一次原始分区表发送的结果：目标是否接受、表内记录数、所用的模式字节，以及可显示文本。</para>
/// </summary>
/// <param name="Accepted"><see langword="true"/> for status 0 or 1. <para>状态为 0 或 1 时为
/// <see langword="true"/>。</para></param>
/// <param name="PartitionCount">The record count the table parsed into. <para>表解析出的记录数。</para></param>
/// <param name="OverrideExisting"><see langword="true"/> when mode byte 0x01 was
/// sent. <para>发送的是覆盖模式（0x01）时为 <see langword="true"/>。</para></param>
/// <param name="Summary">The result text. <para>结果文本。</para></param>
internal sealed record EdlStreamingPartitionTableResult(
    bool Accepted,
    int PartitionCount,
    bool OverrideExisting,
    string Summary);

/// <summary>
/// The outcome of one read-only bad-block scan: how many blocks were inspected, which of them carry a marker,
/// the policy the scan ran under, and renderable text.
/// <para>一次只读坏块扫描的结果：检查过的块数、带坏块标记的块号、所依据的坏块策略，以及可显示文本。</para>
/// </summary>
/// <param name="BlocksScanned">The number of blocks inspected. <para>检查过的块数。</para></param>
/// <param name="BadBlocks">The blocks carrying a bad marker. <para>带坏块标记的块号。</para></param>
/// <param name="Policy">The bad-block policy the scan ran under. <para>本次扫描使用的坏块处理策略。</para></param>
/// <param name="Summary">The result text. <para>结果文本。</para></param>
internal sealed record EdlStreamingBadBlockMapResult(
    int BlocksScanned,
    IReadOnlyList<uint> BadBlocks,
    StreamingBadBlockPolicy Policy,
    string Summary);

/// <summary>
/// The outcome of one bad-marker write or un-write: the block, whether this call actually changed anything,
/// whether it was a mark or an un-mark, and renderable text.
/// <para>一次坏块标记写入或解除的结果：块号、本次是否真正改动、是标记还是解除，以及可显示文本。</para>
/// </summary>
/// <param name="Block">The block that was addressed. <para>被处理的块号。</para></param>
/// <param name="NewlyApplied"><see langword="true"/> when this call wrote the marker or completed the
/// erase. <para>本次确实写入标记或完成擦除时为 <see langword="true"/>。</para></param>
/// <param name="Marking"><see langword="true"/> for a marker write, <see langword="false"/> for the un-mark
/// erase. <para>标记写入为 <see langword="true"/>，解除标记的整块擦除为
/// <see langword="false"/>。</para></param>
/// <param name="Summary">The result text. <para>结果文本。</para></param>
internal sealed record EdlStreamingBadBlockWriteResult(
    uint Block,
    bool NewlyApplied,
    bool Marking,
    string Summary);

/// <summary>
/// The outcome of one NAND configuration read: the three raw configuration words, the decoded fields, the
/// candidate values for the reported sector size, and result text.
/// <para>一次 NAND 配置字读取的结果：三个原始配置字、解码字段、按上报扇区尺寸得到的候选值，以及结果文本。</para>
/// </summary>
/// <param name="Dev0Cfg0">The raw NAND_DEV0_CFG0 word. <para>原始 NAND_DEV0_CFG0。</para></param>
/// <param name="Dev0Cfg1">The raw NAND_DEV0_CFG1 word. <para>原始 NAND_DEV0_CFG1。</para></param>
/// <param name="Dev0EccCfg">The raw NAND_DEV0_ECC_CFG word. <para>原始 NAND_DEV0_ECC_CFG。</para></param>
/// <param name="ReportedUserDataSize">The value the CFG0 user-data field
/// reads. <para>CFG0 用户数据域读到的值。</para></param>
/// <param name="UsesBchEcc"><see langword="true"/> when CFG1 bit 27 selects BCH
/// ECC. <para>CFG1 位 27 表示使用 BCH ECC 时为 <see langword="true"/>。</para></param>
/// <param name="CandidateCfg0">The candidate CFG0 with the reported sector size applied to the user-data
/// field; not written. <para>按上报扇区尺寸套用用户数据域后的候选 CFG0，未写入。</para></param>
/// <param name="CandidateEccCfg">The candidate ECC_CFG after projecting the same size; not
/// written. <para>同一尺寸投影到 ECC_CFG 后的候选值，未写入。</para></param>
/// <param name="BchResetPulseValue">The ECC_CFG value a <c>bch_reset</c> pulse would write; not
/// written. <para>一次 <c>bch_reset</c> 脉冲会写入的 ECC_CFG 值，未写入。</para></param>
/// <param name="Summary">The result text. <para>结果文本。</para></param>
internal sealed record EdlStreamingNandRegisterResult(
    uint Dev0Cfg0,
    uint Dev0Cfg1,
    uint Dev0EccCfg,
    uint ReportedUserDataSize,
    bool UsesBchEcc,
    uint CandidateCfg0,
    uint CandidateEccCfg,
    uint BchResetPulseValue,
    string Summary);

/// <summary>
/// The outcome of one codeword status read: the block, the two status words read back, the bad-block verdict,
/// the ECC corrected-bit count, and text stating that only a page-read sequence ran.
/// <para>一次码字状态读取的结果：块号、两个读回的状态字、坏块判定、ECC 纠正位数，以及说明只执行了页读序列的
/// 文本。</para>
/// </summary>
/// <param name="Block">The block that was read. <para>被读取的块号。</para></param>
/// <param name="BufferStatus">The raw NAND_BUFFER_STATUS word. <para>原始 NAND_BUFFER_STATUS。</para></param>
/// <param name="FlashCommandValue">The word read back from NAND_FLASH_CMD. <para>从
/// NAND_FLASH_CMD 读回的字。</para></param>
/// <param name="IsBadBlock"><see langword="true"/> when the status carries a bad
/// marker. <para>状态带坏块标记时为 <see langword="true"/>。</para></param>
/// <param name="EccCorrectedBits">The corrected-bit count, or -1 for an uncorrectable
/// codeword. <para>纠正位数，不可纠错时为 -1。</para></param>
/// <param name="Summary">The result text. <para>结果文本。</para></param>
internal sealed record EdlStreamingNandEccResult(
    uint Block,
    uint BufferStatus,
    uint FlashCommandValue,
    bool IsBadBlock,
    int EccCorrectedBits,
    string Summary);