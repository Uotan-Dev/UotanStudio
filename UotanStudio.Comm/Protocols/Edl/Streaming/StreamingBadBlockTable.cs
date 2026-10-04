namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Policy the streaming transfer applies when a NAND block carries a bad-block marker,
/// mirroring the reference <c>BadFlags</c> values used by <c>flash_read</c> (BadFlags at
/// nand_config.py:52-57, policy branches at streaming.py:498-508).
/// <para>流式传输在 NAND 块带有坏块标记时应用的策略，对应参考 <c>BadFlags</c> 中
/// <c>flash_read</c> 实际使用的取值（BadFlags 见 nand_config.py:52-57，策略分支见
/// streaming.py:498-508）。</para>
/// </summary>
internal enum StreamingBadBlockPolicy
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
/// Bad-block table for the streaming NAND path: cached block checks, marker writes, and
/// un-marking through block erase, transcribed from <c>edlclient/Library/streaming.py</c>
/// (check_block at streaming.py:401-407, write_badmark at streaming.py:448-468, mark_bad at
/// streaming.py:470-474, unmark_bad at streaming.py:476-480, nand_reset at streaming.py:388-389,
/// block_erase at streaming.py:609-619).
/// <para>流式 NAND 路径的坏块表：带缓存的坏块检测、经块擦除的标记写入与解除标记，转写自
/// <c>edlclient/Library/streaming.py</c>（check_block 见 streaming.py:401-407，write_badmark 见
/// streaming.py:448-468，mark_bad 见 streaming.py:470-474，unmark_bad 见 streaming.py:476-480，
/// nand_reset 见 streaming.py:388-389，block_erase 见 streaming.py:609-619）。</para>
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
/// <param name="sectorsPerPage">The number of codewords per page (streaming.py:462).
/// <para>每页的码字数（streaming.py:462）。</para></param>
/// <param name="badBlockByteNum">The bad-marker byte position used to restore hardware detection
/// (streaming.py:558). <para>恢复硬件坏块检测时使用的标记字节位置（streaming.py:558）。</para></param>
/// <param name="badBlockInSpareArea">1 when the marker lives in the spare area, else 0
/// (streaming.py:559). <para>标记位于 spare 区时为 1，否则为 0（streaming.py:559）。</para></param>
/// <param name="policy">The bad-block handling policy. <para>坏块处理策略。</para></param>
internal sealed class StreamingBadBlockTable(
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
    /// Host-side bound for controller status polls; the reference <c>nandwait</c> loops forever
    /// (streaming.py:372-374), which cannot be transcribed against untrusted targets.
    /// <para>控制器状态轮询的主机侧上限；参考的 <c>nandwait</c> 是无限循环
    /// （streaming.py:372-374），对不可信目标不能照搬。</para>
    /// </summary>
    private const int MaxStatusPolls = 100_000;

    private readonly Dictionary<uint, bool> _cache = [];

    /// <summary>
    /// Gets the configured handling policy.
    /// <para>获取所配置的坏块处理策略。</para>
    /// </summary>
    /// <value>The bad-block policy. <para>坏块策略。</para></value>
    internal StreamingBadBlockPolicy Policy => policy;

    /// <summary>
    /// Reads the cached verdict for a block, or performs a fresh controller page-read check when
    /// the policy consults markers (streaming.py:496-512).
    /// <para>读取块的缓存结论；当策略需要查询标记时执行全新的控制器页读检测
    /// （streaming.py:496-512）。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the block carries a bad marker.
    /// <para>块带有坏块标记时为 <see langword="true"/>。</para></returns>
    /// <exception cref="StreamingException">Thrown when the controller never reports idle or the
    /// status register cannot be read. <para>控制器始终不报告空闲，或状态寄存器无法读取时抛出。</para></exception>
    internal async Task<bool> CheckBlockAsync(uint block, CancellationToken cancellationToken)
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
    /// Writes the bad-block marker into a currently good block, mirroring
    /// <c>mark_bad</c> and <c>write_badmark</c>: ECC is disabled, hardware detection is masked
    /// off, the 0x220-byte fill buffer is programmed once per codeword, then detection and the
    /// saved configs are restored (streaming.py:448-474).
    /// <para>向当前完好的块写入坏块标记，对应 <c>mark_bad</c> 与 <c>write_badmark</c>：禁用 ECC，
    /// 掩掉硬件检测，每个码字编程一次 0x220 字节的填充缓冲区，随后恢复检测与保存的配置
    /// （streaming.py:448-474）。</para>
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
    internal async Task<bool> MarkBadAsync(uint block, bool confirm, CancellationToken cancellationToken)
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
    /// <c>block_erase</c> (streaming.py:476-480, 609-619).
    /// <para>擦除整个块以清除坏块标记，对应 <c>unmark_bad</c> 与 <c>block_erase</c>
    /// （streaming.py:476-480、609-619）。</para>
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
    internal async Task<bool> UnmarkBadAsync(uint block, bool confirm, CancellationToken cancellationToken)
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