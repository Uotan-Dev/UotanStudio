namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Policy the streaming transfer applies when a NAND block carries a bad-block marker.
/// <para>流式传输在 NAND 块带有坏块标记时应用的策略。</para>
/// </summary>
public enum StreamingBadBlockPolicy
{
    /// <summary>
    /// Turn hardware bad-block detection off and stream the raw array (BAD_DISABLE branch,
    /// streaming.py:498-499). <para>关闭硬件坏块检测并直接传输原始阵列（BAD_DISABLE 分支，
    /// streaming.py:498-499）。</para></summary>
    Disable,

    /// <summary>
    /// Never consult the marker table; blocks are read as-is (BAD_IGNORE branch,
    /// streaming.py:500). <para>从不查询标记表；块按原样读取（BAD_IGNORE 分支，
    /// streaming.py:500）。</para></summary>
    Ignore,

    /// <summary>
    /// Test each fresh block, cache the verdict, and treat bad blocks as fill bytes
    /// (default branch, streaming.py:501-512). <para>检测每个新块并缓存结论，坏块以填充字节
    /// 处理（默认分支，streaming.py:501-512）。</para></summary>
    Mark
}

/// <summary>
/// Bad-block table for the streaming NAND path: cached block checks, marker writes, un-marking through block erase, and two read-only paths reporting register and ECC state without programming an array location.
/// <para>流式 NAND 路径的坏块表：带缓存的坏块检测、经块擦除的标记写入与解除标记，以及两条只读路径——仅回报寄存器与 ECC 状态，不编程任何阵列位置。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="StreamingBadBlockTable"/> class bound to one
/// streaming session and its controller layout.
/// <para>初始化绑定单个流式会话及其控制器布局的 <see cref="StreamingBadBlockTable"/> 类的新实例。</para>
/// </remarks>
/// <param name="protocol">The streaming session in patched (memory-writing) link mode.
/// <para>处于打补丁（可写内存）链接模式的流式会话。</para></param>
/// <param name="nandBase">The physical base address of the NAND controller registers.
/// <para>NAND 控制器寄存器的物理基地址。</para></param>
/// <param name="pagesPerBlock">The number of pages inside one erase block.
/// <para>一个擦除块内的页数。</para></param>
/// <param name="sectorsPerPage">The number of codewords per page.
/// <para>每页的码字数。</para></param>
/// <param name="badBlockByteNum">The bad-marker byte position used to restore hardware detection
///. <para>恢复硬件坏块检测时使用的标记字节位置。</para></param>
/// <param name="badBlockInSpareArea">1 when the marker lives in the spare area, else 0
///. <para>标记位于 spare 区时为 1，否则为 0。</para></param>
/// <param name="policy">The bad-block handling policy. <para>坏块处理策略。</para></param>
public sealed class StreamingBadBlockTable(
    StreamingProtocol protocol,
    uint nandBase,
    uint pagesPerBlock,
    uint sectorsPerPage,
    uint badBlockByteNum,
    uint badBlockInSpareArea,
    StreamingBadBlockPolicy policy)
{
    /// <summary>
    /// Controller register offsets transcribed from nand_config.py:418-446.
    /// <para>转写自 nand_config.py:418-446 的控制器寄存器偏移。</para>
    /// </summary>
    private const int FlashCmdOffset = 0x00;
    private const int Addr0Offset = 0x04;
    private const int Addr1Offset = 0x08;
    private const int ExecOffset = 0x10;
    private const int FlashStatusOffset = 0x14;
    private const int BufferStatusOffset = 0x18;
    private const int Cfg0Offset = 0x20;
    private const int Cfg1Offset = 0x24;
    private const int EccCfgOffset = 0x28;
    private const int FlashBufferOffset = 0x100;

    /// <summary>
    /// Controller command codes transcribed from nand_config.py:404-412.
    /// <para>转写自 nand_config.py:404-412 的控制器命令码。</para>
    /// </summary>
    private const uint CmdSoftReset = 0x01;
    private const uint CmdPageReadAll = 0x34;
    private const uint CmdProgramPageAll = 0x39;
    private const uint CmdBlockErase = 0x3A;

    /// <summary>
    /// Host-side bound for controller status polls; an unbounded poll loop cannot be justified
    /// against untrusted targets.
    /// <para>控制器状态轮询的主机侧上限；对不可信目标不能采用无限轮询循环。</para>
    /// </summary>
    private const int MaxStatusPolls = 100_000;

    private readonly Dictionary<uint, bool> _cache = [];

    /// <summary>
    /// Gets the configured handling policy.
    /// <para>获取所配置的坏块处理策略。</para>
    /// </summary>
    /// <value>The bad-block policy. <para>坏块策略。</para></value>
    public StreamingBadBlockPolicy Policy => policy;

    /// <summary>
    /// Reads the cached verdict for a block, or performs a fresh controller page-read check when
    /// the policy consults markers.
    /// <para>读取块的缓存结论；当策略需要查询标记时执行全新的控制器页读检测
    /// 。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the block carries a bad marker.
    /// <para>块带有坏块标记时为 <see langword="true"/>。</para></returns>
    /// <exception cref="StreamingException">Thrown when the controller never reports idle or the
    /// status register cannot be read. <para>控制器始终不报告空闲，或状态寄存器无法读取时抛出。</para></exception>
    public async Task<bool> CheckBlockAsync(uint block, CancellationToken cancellationToken)
    {
        if (policy == StreamingBadBlockPolicy.Disable || policy == StreamingBadBlockPolicy.Ignore)
        {
            return false;
        }

        if (_cache.TryGetValue(block, out var cached))
        {
            return cached;
        }

        await NandResetAsync(cancellationToken).ConfigureAwait(false);
        await SetAddressAsync(block, page: 0, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(FlashCmdOffset, CmdPageReadAll, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(ExecOffset, 1, cancellationToken).ConfigureAwait(false);
        await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
        var bufferStatus = await PeekRegisterAsync(BufferStatusOffset, cancellationToken).ConfigureAwait(false);
        var bad = StreamingNandRegisters.IsBadBlock(bufferStatus, isWideFlash: false);
        _cache[block] = bad;
        return bad;
    }

    /// <summary>
    /// Writes the bad-block marker into a currently good block.
    /// <para>向当前完好的块写入坏块标记。</para>
    /// ECC is disabled, hardware detection is masked off, the 0x220-byte fill buffer is programmed
    /// once per codeword, then detection and the saved configs are restored.
    /// <para>禁用 ECC，掩掉硬件检测，每个码字编程一次 0x220 字节的填充缓冲区，随后恢复检测与保存的
    /// 配置。</para>
    /// </summary>
    /// <param name="block">The block to mark. <para>要标记的块。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the marker write.
    /// <para>传 <see langword="true"/> 以确认标记写入。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the block was freshly marked, <see langword="false"/>
    /// when it already carried a marker. <para>块是本次新标记时为
    /// <see langword="true"/>；已带标记时为 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is
    /// <see langword="false"/>; the refusal happens before any transport write.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/> 时抛出；拒绝发生在任何传输写出
    /// 之前。</para></exception>
    public async Task<bool> MarkBadAsync(uint block, bool confirm, CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            throw new ArgumentException("Marking a NAND block bad requires explicit confirmation.", nameof(confirm));
        }

        if (await CheckBlockAsync(block, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var cfg1 = await PeekRegisterAsync(Cfg1Offset, cancellationToken).ConfigureAwait(false);
        var eccCfg = await PeekRegisterAsync(EccCfgOffset, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(EccCfgOffset, StreamingNandRegisters.ApplyEccDisable(eccCfg, 1), cancellationToken)
            .ConfigureAwait(false);
        await PokeRegisterAsync(Cfg1Offset, StreamingNandRegisters.ApplyEccDisable(cfg1, 1), cancellationToken)
            .ConfigureAwait(false);
        await PokeRegisterAsync(Cfg1Offset, StreamingNandRegisters.DisableHardwareBadBlock(cfg1), cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[StreamingConstants.BadMarkBufferSize];
        buffer[0] = StreamingConstants.BadMarkFillByte;
        Array.Fill(buffer, (byte)0, 1, buffer.Length - 1);
        await NandResetAsync(cancellationToken).ConfigureAwait(false);
        await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
        await SetAddressAsync(block, page: 0, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(FlashCmdOffset, CmdProgramPageAll, cancellationToken).ConfigureAwait(false);
        for (var sector = 0u; sector < sectorsPerPage; sector++)
        {
            await protocol.MemWriteAsync(nandBase + FlashBufferOffset, buffer, confirm: true, cancellationToken)
                .ConfigureAwait(false);
            await PokeRegisterAsync(ExecOffset, 1, cancellationToken).ConfigureAwait(false);
            await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
        }

        var restoredCfg1 = StreamingNandRegisters.EnableHardwareBadBlock(cfg1, badBlockByteNum, badBlockInSpareArea);
        await PokeRegisterAsync(Cfg1Offset, restoredCfg1, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(EccCfgOffset, eccCfg, cancellationToken).ConfigureAwait(false);
        _cache[block] = true;
        return true;
    }

    /// <summary>
    /// Clears a bad marker by erasing the whole block, mirroring <c>unmark_bad</c> and
    /// <c>block_erase</c>.
    /// <para>擦除整个块以清除坏块标记，对应 <c>unmark_bad</c> 与 <c>block_erase</c>
    /// 。</para>
    /// </summary>
    /// <param name="block">The block to erase. <para>要擦除的块。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the erase.
    /// <para>传 <see langword="true"/> 以确认擦除。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when a marked block was erased, <see langword="false"/>
    /// when the block was already good. <para>被标记的块已擦除时为
    /// <see langword="true"/>；块本就完好时为 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is
    /// <see langword="false"/>. <para><paramref name="confirm"/> 为
    /// <see langword="false"/> 时抛出。</para></exception>
    public async Task<bool> UnmarkBadAsync(uint block, bool confirm, CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            throw new ArgumentException("Erasing a NAND block requires explicit confirmation.", nameof(confirm));
        }

        if (!await CheckBlockAsync(block, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await NandResetAsync(cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(Addr0Offset, StreamingNandRegisters.EraseAddress0(block, pagesPerBlock), cancellationToken)
            .ConfigureAwait(false);
        await PokeRegisterAsync(Addr1Offset, 0, cancellationToken).ConfigureAwait(false);
        var cfg0 = await PeekRegisterAsync(Cfg0Offset, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(Cfg0Offset, StreamingNandRegisters.BlockEraseCfg0(cfg0), cancellationToken)
            .ConfigureAwait(false);
        await PokeRegisterAsync(FlashCmdOffset, CmdBlockErase, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(ExecOffset, 1, cancellationToken).ConfigureAwait(false);
        await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(Cfg0Offset, cfg0, cancellationToken).ConfigureAwait(false);
        _cache.Remove(block);
        return true;
    }

    /// <summary>
    /// Reads the three NAND_DEV0 configuration words without programming anything.
    /// <para>只读取 NAND_DEV0 的三个配置字，不做任何编程。</para>
    /// Only the peek path runs, so no controller command, page address or buffer byte is written. The
    /// host decodes and derives from these words with <see cref="StreamingNandRegisters"/>, and nothing
    /// this method returns is written back.
    /// <para>仅走 peek 路径，因此不会写入控制器命令、页地址或缓冲字节。宿主用
    /// <see cref="StreamingNandRegisters"/> 解码并推导这些字，本方法返回的任何内容都不会被写回。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The three raw configuration words. <para>三个原始配置字。</para></returns>
    /// <exception cref="StreamingException">Thrown when a register read fails or the reply is
    /// malformed. <para>寄存器读取失败或回包格式错误时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<StreamingNandRegisterSnapshot> ReadRegisterSnapshotAsync(CancellationToken cancellationToken)
    {
        var cfg0 = await PeekRegisterAsync(Cfg0Offset, cancellationToken).ConfigureAwait(false);
        var cfg1 = await PeekRegisterAsync(Cfg1Offset, cancellationToken).ConfigureAwait(false);
        var eccCfg = await PeekRegisterAsync(EccCfgOffset, cancellationToken).ConfigureAwait(false);
        return new StreamingNandRegisterSnapshot(cfg0, cfg1, eccCfg);
    }

    /// <summary>
    /// Runs one controller page-read command sequence over a block and reports the resulting status
    /// words.
    /// <para>对一个块执行一次控制器页读命令序列并回报随后的状态字。</para>
    /// The sequence is soft reset, page address, PAGE_READ_ALL, exec, bounded status wait, then a
    /// read-back of NAND_BUFFER_STATUS and NAND_FLASH_CMD. No array location is programmed or erased
    /// here, but the controller command registers are pulsed, so the caller must confirm the sequence.
    /// <para>序列为软复位、页地址、PAGE_READ_ALL、执行、有界状态等待，然后读回 NAND_BUFFER_STATUS 与
    /// NAND_FLASH_CMD。这里不编程也不擦除任何阵列位置，但会脉冲控制器命令寄存器，因此调用方必须确认
    /// 该序列。</para>
    /// </summary>
    /// <param name="block">The block whose page zero is read. <para>读取其 0 页的块。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the controller command
    /// sequence. <para>传 <see langword="true"/> 以确认这次控制器命令序列。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The bad-block and ECC verdict decoded from the read-back words.
    /// <para>由读回字解码出的坏块与 ECC 结论。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is
    /// <see langword="false"/>; the refusal happens before any byte reaches the wire.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/> 时抛出；拒绝发生在任何字节上线
    /// 之前。</para></exception>
    /// <exception cref="StreamingException">Thrown when the controller never reports idle.
    /// <para>控制器始终不报告空闲时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<StreamingNandCodewordStatus> ReadCodewordStatusAsync(uint block, bool confirm,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            throw new ArgumentException(
                "Pulsing the streaming NAND controller for a codeword read requires explicit confirmation.",
                nameof(confirm));
        }

        await NandResetAsync(cancellationToken).ConfigureAwait(false);
        await SetAddressAsync(block, page: 0, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(FlashCmdOffset, CmdPageReadAll, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(ExecOffset, 1, cancellationToken).ConfigureAwait(false);
        await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
        var bufferStatus = await PeekRegisterAsync(BufferStatusOffset, cancellationToken).ConfigureAwait(false);
        var flashCommand = await PeekRegisterAsync(FlashCmdOffset, cancellationToken).ConfigureAwait(false);
        return new StreamingNandCodewordStatus(block, bufferStatus, flashCommand,
            StreamingNandRegisters.IsBadBlock(bufferStatus, isWideFlash: false),
            StreamingNandRegisters.EvaluateEccStatus(bufferStatus, flashCommand));
    }

    /// <summary>
    /// Runs the soft-reset controller command (nand_reset via exec_nand, streaming.py:383-389).
    /// <para>执行软复位控制器命令（经 exec_nand 的 nand_reset，streaming.py:383-389）。</para>
    /// </summary>
    private async Task NandResetAsync(CancellationToken cancellationToken)
    {
        await PokeRegisterAsync(FlashCmdOffset, CmdSoftReset, cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(ExecOffset, 1, cancellationToken).ConfigureAwait(false);
        await WaitForControllerAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Programs the page address registers (set_address, streaming.py:377-380).
    /// <para>写入页地址寄存器（set_address，streaming.py:377-380）。</para>
    /// </summary>
    private async Task SetAddressAsync(uint block, uint page, CancellationToken cancellationToken)
    {
        await PokeRegisterAsync(Addr0Offset,
            StreamingNandRegisters.PageAddress0(block, page, pagesPerBlock), cancellationToken).ConfigureAwait(false);
        await PokeRegisterAsync(Addr1Offset,
            StreamingNandRegisters.PageAddress1(block, page, pagesPerBlock), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls NAND_FLASH_STATUS until the busy nibble clears, bounded by
    /// <see cref="MaxStatusPolls"/> (nandwait, streaming.py:372-374).
    /// <para>轮询 NAND_FLASH_STATUS 直到忙碌半字节清零，上限为
    /// <see cref="MaxStatusPolls"/>（nandwait，streaming.py:372-374）。</para>
    /// </summary>
    private async Task WaitForControllerAsync(CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < MaxStatusPolls; poll++)
        {
            var status = await PeekRegisterAsync(FlashStatusOffset, cancellationToken).ConfigureAwait(false);
            if ((status & 0xF) == 0)
            {
                return;
            }
        }

        throw new StreamingException(EdlErrorCode.ConnectionLost,
            "The streaming NAND controller never reported an idle status.");
    }

    /// <summary>
    /// Reads one controller register through the session's peek path.
    /// <para>经会话的 peek 路径读取一个控制器寄存器。</para>
    /// </summary>
    private async Task<uint> PeekRegisterAsync(int offset, CancellationToken cancellationToken)
        => await protocol.MemPeekAsync(nandBase + (uint)offset, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Writes one controller register through the session's poke path. The register writes here
    /// are controller state, not user storage, so they reuse the already-confirmed NAND flow
    /// without a second confirmation prompt.
    /// <para>经会话的 poke 路径写入一个控制器寄存器。这里的寄存器写的是控制器状态而非用户存储，
    /// 因此复用已确认的 NAND 流程，不再二次确认。</para>
    /// </summary>
    private async Task PokeRegisterAsync(int offset, uint value, CancellationToken cancellationToken)
        => await protocol.MemPokeAsync(nandBase + (uint)offset, value, confirm: true, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
/// The three NAND_DEV0 configuration words read back from one streaming session's NAND controller,
/// raw and undecoded: NAND_DEV0_CFG0, NAND_DEV0_CFG1 and NAND_DEV0_ECC_CFG.
/// Decoding and any candidate derivation belong to the caller, which uses
/// <see cref="StreamingNandRegisters"/>; this record never implies that a value was written.
/// <para>从单个流式会话的 NAND 控制器读回的两个 NAND_DEV0 配置字与 ECC 配置字，保持原始未解码：
/// NAND_DEV0_CFG0、NAND_DEV0_CFG1 与 NAND_DEV0_ECC_CFG。解码与任何候选值
/// 推导都归调用方，由其使用 <see cref="StreamingNandRegisters"/>；本记录绝不表示某个值已被写入。</para>
/// </summary>
/// <param name="Dev0Cfg0">The raw NAND_DEV0_CFG0 word. <para>原始 NAND_DEV0_CFG0 字。</para></param>
/// <param name="Dev0Cfg1">The raw NAND_DEV0_CFG1 word. <para>原始 NAND_DEV0_CFG1 字。</para></param>
/// <param name="Dev0EccCfg">The raw NAND_DEV0_ECC_CFG word. <para>原始 NAND_DEV0_ECC_CFG 字。</para></param>
public sealed record StreamingNandRegisterSnapshot(uint Dev0Cfg0, uint Dev0Cfg1, uint Dev0EccCfg);

/// <summary>
/// The status words and decoded verdicts after one controller page-read sequence: the raw
/// NAND_BUFFER_STATUS and NAND_FLASH_CMD read-backs, the bad-marker verdict from
/// <see cref="StreamingNandRegisters.IsBadBlock"/>, and the ECC verdict from
/// <see cref="StreamingNandRegisters.EvaluateEccStatus"/>. Both
/// verdicts assume the narrow-flash comparison, because
/// the table does not read the CFG1 wide-flash bit.
/// <para>一次控制器页读序列之后的状态字与解码结论：原始的 NAND_BUFFER_STATUS 与 NAND_FLASH_CMD
/// 读回值、由 <see cref="StreamingNandRegisters.IsBadBlock"/> 得到的坏标记结论，以及由
/// <see cref="StreamingNandRegisters.EvaluateEccStatus"/> 得到的 ECC 结论。两个结论都按窄闪存比较，因为该表不读取 CFG1 的
/// 宽闪存位。</para>
/// </summary>
/// <param name="Block">The block whose page zero was read. <para>读取其 0 页的块。</para></param>
/// <param name="BufferStatus">The raw NAND_BUFFER_STATUS word. <para>原始 NAND_BUFFER_STATUS 字。</para></param>
/// <param name="FlashCommandValue">The word read back from NAND_FLASH_CMD. <para>从 NAND_FLASH_CMD
/// 读回的字。</para></param>
/// <param name="IsBadBlock"><see langword="true"/> when the status carries a bad marker.
/// <para>状态携带坏块标记时为 <see langword="true"/>。</para></param>
/// <param name="EccCorrectedBits">The corrected-bit count, or -1 for an uncorrectable codeword.
/// <para>纠正位数；不可纠错时为 -1。</para></param>
public sealed record StreamingNandCodewordStatus(
    uint Block,
    uint BufferStatus,
    uint FlashCommandValue,
    bool IsBadBlock,
    int EccCorrectedBits);