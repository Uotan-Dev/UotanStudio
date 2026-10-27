using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Upload;
using UotanStudio.Hash.Sums;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Preloader command-stage register and memory access over the boot byte protocol: the read, write,
/// extended-command, jump, watchdog-disable and return-to-BROM sequences this client exposes
/// while the device presents its preloader command interface.
/// <para>boot 字节协议之上的 preloader 命令阶段寄存器与内存访问：设备呈现 preloader 命令接口期间，
/// 本客户端所提供的读取、写入、扩展命令、跳转、看门狗禁用与回到 BROM 序列。</para>
/// </summary>
/// <remarks>
/// Every framing here is a transcription of : the read
/// paths mirror <c>read</c> (lines 388-405), the write paths mirror <c>write</c> (lines 416-442),
/// <c>writemem</c> mirrors lines 450-455, <c>run_ext_cmd</c> mirrors lines 484-490, <c>jump_bl</c>
/// mirrors lines 492-499, <c>jump_to_partition</c> mirrors lines 501-509,
/// <c>setreg_disablewatchdogtimer</c> mirrors lines 532-655 through
/// <see cref="MtkPreloaderWatchdogTable"/>, and <c>reset_to_brom</c> mirrors lines 457-482.
/// Every path here raises a protocol fault on the first refusal, truncation, or echo mismatch
/// and faults the session so no further command bytes are exchanged. Commands are only accepted while
/// the session sits in the bootloader stage with no download-agent upload begun, so these paths can
/// never interleave with XFlash, XML or Legacy bring-up on the same transport.
/// <para>此处的每个帧结构都转录自 ：读取路径对应 <c>read</c>
/// （第 388-405 行），写入路径对应 <c>write</c>（第 416-442 行），<c>writemem</c> 对应第 450-455 行，
/// <c>run_ext_cmd</c> 对应第 484-490 行，<c>jump_bl</c> 对应第 492-499 行，<c>jump_to_partition</c>
/// 对应第 501-509 行，<c>setreg_disablewatchdogtimer</c> 经由 <see cref="MtkPreloaderWatchdogTable"/>
/// 对应第 532-655 行，<c>reset_to_brom</c> 对应第 457-482 行。
/// 此处每条路径在首次拒绝、截断或回显不匹配时都抛出协议错误并使会话进入 fault 状态，
/// 不再交换任何命令字节。命令仅在会话处于 bootloader 阶段且尚未开始任何下载代理上传时被接受，
/// 因此这些路径绝不可能在同一传输上与 XFlash、XML 或 Legacy 启动交错。</para>
/// </remarks>
public sealed partial class MtkSession
{
    private const byte PreloaderRead16Command = 0xD0;
    private const byte PreloaderRead32Command = 0xD1;
    private const byte PreloaderWrite16Command = 0xD2;
    private const byte PreloaderWrite32Command = 0xD4;
    private const byte PreloaderExtendedCommand = 0xC8;
    private const byte PreloaderJumpBootLoaderCommand = 0xD6;
    private const byte PreloaderJumpToPartitionCommand = 0x71;
    private const byte PreloaderSendPartitionDataCommand = 0x70;
    private const byte PreloaderReadA2Command = 0xA2;
    private const int PreloaderPartitionChunkBytes = 0x200;
    private const byte PreloaderCacheControlOperand = 0xB1;
    private const int PreloaderPartitionNameBytes = 64;
    private const int PreloaderPeekChunkBytes = 512;
    private const ushort PreloaderWriteAcceptedStatus = 3;
    private const uint UsbdlBitEnable = 0x0000_0001;
    private const uint UsbdlBitBootLoader = 0x0000_0002;
    private const uint UsbdlTimeoutMask = 0x0000_FFFC;
    private const uint UsbdlTimeoutMax = UsbdlTimeoutMask >> 2;
    private const uint UsbdlMagic = 0x444C_0000;
    private const uint MiscLockKeyMagic = 0xAD98;
    private const uint Mtk6261EmiRemapRegister = 0xA051_0000;
    private const uint Mtk6261InternalFlashMapBit = 0x2;

    /// <summary>Reads consecutive sixteen-bit register words through the preloader read command.
    /// <para>通过 preloader 读取命令读取连续的十六位寄存器字。</para></summary>
    /// <remarks>The framing mirrors <c>read</c>with <c>length == 16</c>: the command byte, the big-endian address and
    /// the big-endian word count are each echoed, then one status word, the big-endian payload, and a
    /// closing status word arrive. Where that exchange returns an empty list after a refusal, this path
    /// raises a protocol fault and faults the session. 帧结构对应 <c>length == 16</c>的 <c>read</c>： 命令字节、大端地址与大端字数逐一回显，随后到达一个状态字、大端载荷与收尾状态字。</remarks>
    /// <param name="address">The first register address; words walk upward by two bytes.
    /// <para>起始寄存器地址；字按两字节向上递增。</para></param>
    /// <param name="wordCount">The positive word count, limited so one exchange stays within
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>bytes. 正数字数，其上限保证单次交换不超过
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>字节。</param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The values exactly as the big-endian wire delivered them.
    /// <para>与大端线路交付一致的取值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The word count is not positive or the window leaves the 32-bit address space; no byte was sent.
    /// <para>字数非正，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">An echo, status word or the payload was invalid; the session faults.
    /// <para>回显、状态字或载荷无效；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<ushort[]> ReadPreloader16Async(uint address, int wordCount,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wordCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wordCount,
            MtkDaMemoryAccess.MaximumWordLoopReadBytes / sizeof(ushort));
        RequireDaMemoryWindow(address, checked(wordCount * sizeof(ushort)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var payload = await ReadPreloaderWordsOwnedAsync(PreloaderRead16Command, address, wordCount,
                    sizeof(ushort), cancellationToken).ConfigureAwait(false);
                var values = new ushort[wordCount];
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(index * sizeof(ushort)));
                }

                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader READ16 0x{address:X8} x{wordCount}.");
                return values;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads consecutive thirty-two-bit register words through the preloader read command.
    /// <para>通过 preloader 读取命令读取连续的三十二位寄存器字。</para></summary>
    /// <remarks>The framing mirrors <c>read</c>with <c>length == 32</c> and is the same exchange exploit drivers use. Where that exchange returns an empty list after a refusal, this path raises
    /// a protocol fault and faults the session. 帧结构对应 <c>length == 32</c>的 <c>read</c>， 与漏洞驱动使用的交换相同。</remarks>
    /// <param name="address">The first doubleword address; words walk upward by four bytes.
    /// <para>起始双字地址；字按四字节向上递增。</para></param>
    /// <param name="dwordCount">The positive doubleword count, limited so one exchange stays within
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>bytes. 正数双字数，其上限保证单次交换不超过
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>字节。</param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The values exactly as the big-endian wire delivered them.
    /// <para>与大端线路交付一致的取值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The doubleword count is not positive or the window leaves the 32-bit address space; no byte was sent.
    /// <para>双字数非正，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">An echo, status word or the payload was invalid; the session faults.
    /// <para>回显、状态字或载荷无效；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<uint[]> ReadPreloader32Async(uint address, int dwordCount,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dwordCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dwordCount,
            MtkDaMemoryAccess.MaximumWordLoopReadBytes / MtkDaMemoryAccess.RegisterCellBytes);
        RequireDaMemoryWindow(address, checked(dwordCount * MtkDaMemoryAccess.RegisterCellBytes));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var payload = await ReadPreloaderWordsOwnedAsync(PreloaderRead32Command, address, dwordCount,
                    MtkDaMemoryAccess.RegisterCellBytes, cancellationToken).ConfigureAwait(false);
                var values = new uint[dwordCount];
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = BinaryPrimitives.ReadUInt32BigEndian(
                        payload.AsSpan(index * MtkDaMemoryAccess.RegisterCellBytes));
                }

                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader READ32 0x{address:X8} x{dwordCount}.");
                return values;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Runs one extended preloader command through the 0xC8 cache-control exchange.
    /// <para>通过 0xC8 缓存控制交换执行一条扩展 preloader 命令。</para></summary>
    /// <remarks>
    /// <c>run_ext_cmd</c> echoes the opcode and the
    /// operand, then consumes one byte and a two-byte status without interpreting either; the
    /// default operand <c>0xB1</c> refreshes the caches. Because the operand can program
    /// device state, the exchange is only attempted when the caller confirms it.
    /// <c>run_ext_cmd</c>回显操作码与操作数，然后读取
    /// 并丢弃一个字节与一个两字节状态，不做解释；默认操作数 <c>0xB1</c> 用于刷新缓存。由于操作数可能
    /// 编程设备状态，仅在调用方确认后才发起交换。
    /// </remarks>
    /// <param name="operand">The single operand byte; the cache-refresh default is
    /// <c>0xB1</c>. <para>单字节操作数；缓存刷新默认值为 <c>0xB1</c>。</para></param>
    /// <param name="confirm">The explicit caller opt-in that authorizes the command.
    /// <para>授权本命令的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the trailing status bytes were consumed.
    /// <para>在读取完尾部状态字节后完成的任务。</para></returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirm"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">An echo did not match or the reply was truncated; the
    /// session faults. <para>某次回显不匹配或回复被截断；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task RunPreloaderExtendedCommandAsync(byte operand = PreloaderCacheControlOperand,
        bool confirm = false, CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        RequireDaMemoryWriteConfirmation(confirm, "preloader");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await EchoAsync([PreloaderExtendedCommand], cancellationToken).ConfigureAwait(false);
                await EchoAsync([operand], cancellationToken).ConfigureAwait(false);
                _ = await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false);
                _ = await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader extended command 0xC8 with operand 0x{operand:X2}.");
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Asks the preloader to jump to its boot loader through command 0xD6.
    /// <para>通过命令 0xD6 请求 preloader 跳转到其 boot loader。</para></summary>
    /// <remarks>
    /// <c>jump_bl</c> echoes the opcode and then
    /// requires two status words at or below <c>0x00FF</c>; that exchange returns
    /// <see langword="false"/> on refusal while this path faults. The boot transition after the
    /// acknowledgement drops the link, so the host must treat the transport as one-shot; the session
    /// stage is deliberately left unchanged.
    /// <c>jump_bl</c>回显操作码，然后要求两个不超过
    /// <c>0x00FF</c> 的状态字；该交互在拒绝时返回 <see langword="false"/>，而本路径进入 fault。确认后的启动
    /// 转换会断开链路，因此宿主必须把该传输视为一次性；会话阶段有意保持不变。
    /// </remarks>
    /// <param name="confirm">The explicit caller opt-in that authorizes the jump.
    /// <para>授权本次跳转的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after both status words were accepted.
    /// <para>在两个状态字均被接受后完成的任务。</para></returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirm"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The echo or a status was refused; the session faults.
    /// <para>回显或某状态被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task JumpPreloaderToBootLoaderAsync(bool confirm, CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        RequireDaMemoryWriteConfirmation(confirm, "preloader");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await EchoAsync([PreloaderJumpBootLoaderCommand], cancellationToken).ConfigureAwait(false);
                await ReadStatusAsync("preloader jump-to-boot-loader request", cancellationToken)
                    .ConfigureAwait(false);
                await ReadStatusAsync("preloader jump-to-boot-loader completion", cancellationToken)
                    .ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    "preloader jump-to-boot-loader acknowledged.");
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Asks a preloader running as DA to boot the named partition through command 0x71.
    /// <para>通过命令 0x71 请求作为 DA 运行的 preloader 引导指定分区。</para></summary>
    /// <remarks><c>jump_to_partition</c> truncates
    /// the UTF-8 name to 64 bytes, pads it with nulls to exactly 64, echoes the opcode, then writes the
    /// name field without an echo check and accepts a single status word. The partition boot changes
    /// device state, so the exchange is only attempted when the caller confirms it.
    /// <c>jump_to_partition</c>把 UTF-8 名称截断到 64
    /// 字节并用 NUL 补齐到恰好 64 字节，回显操作码后写入名称字段（不校验回显），并接受单个状态字。 分区引导会改变设备状态，因此仅在调用方确认后才发起交换。</remarks>
    /// <param name="partitionName">The partition name; its UTF-8 encoding is truncated or padded to exactly 64 bytes.
    /// <para>分区名称；其 UTF-8 编码被截断或补齐到恰好 64 字节。</para></param>
    /// <param name="confirm">The explicit caller opt-in that authorizes the jump.
    /// <para>授权本次跳转的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the status word was accepted.
    /// <para>在状态字被接受后完成的任务。</para></returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirm"/> is <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> is empty.
    /// <para><paramref name="partitionName"/> 为空。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The echo or status was refused; the session faults.
    /// <para>回显或状态被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task JumpPreloaderToPartitionAsync(string partitionName, bool confirm,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        ArgumentException.ThrowIfNullOrEmpty(partitionName);
        RequireDaMemoryWriteConfirmation(confirm, "preloader");
        var name = EncodePreloaderPartitionName(partitionName);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await EchoAsync([PreloaderJumpToPartitionCommand], cancellationToken).ConfigureAwait(false);
                await WriteAsync(name, _timeoutMs, cancellationToken).ConfigureAwait(false);
                await ReadStatusAsync("preloader jump-to-partition", cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader jump-to-partition {partitionName}.");
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads one register word through the A2 command used for IoT identity.
    /// <para>通过用于 IoT 身份的 A2 命令读取一个寄存器字。</para></summary>
    /// <remarks>
    /// The framing is the A2 command: the command byte, the big-endian address and the big-endian word
    /// count (always one) are each echoed, then one big-endian reply word arrives. The exchange carries
    /// no status words, and only one word is read regardless of the echoed count, so no count parameter
    /// is exposed here.
    /// <para>帧结构为 A2 命令：命令字节、大端地址与大端字数（恒为 1）逐一回显，随后到达一个大端回复字。
    /// 该交互不含任何状态字，且无论回显的字数是多少都只读取一个字，因此此处不暴露字数参数。</para>
    /// </remarks>
    /// <param name="address">The register address; the IoT identity registers live at
    /// <c>0x80000000</c>, <c>0x80000008</c> and <c>0x8000000C</c>.
    /// <para>寄存器地址；IoT 身份寄存器位于 <c>0x80000000</c>、<c>0x80000008</c> 与 <c>0x8000000C</c>。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The reply word exactly as the big-endian wire delivered it.
    /// <para>与大端线路交付一致的回复字。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">An echo did not match or the reply was truncated; the
    /// session faults. <para>某次回显不匹配或回复被截断；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<ushort> ReadPreloaderA2Async(uint address, CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        RequireDaMemoryWindow(address, sizeof(ushort));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await EchoAsync([PreloaderReadA2Command], cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async(address, cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async(1, cancellationToken).ConfigureAwait(false);
                var reply = await ReadAsync(sizeof(ushort), _timeoutMs, cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader READ_A2 0x{address:X8}.");
                return BinaryPrimitives.ReadUInt16BigEndian(reply);
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads consecutive thirty-two-bit register words and keeps the raw big-endian wire bytes.
    /// <para>读取连续的三十二位寄存器字，并保留原始大端线路字节。</para></summary>
    /// <remarks>The framing mirrors <c>read</c>with <c>length == 32</c>and <c>direct=True</c>: the exchange is identical to
    /// <see cref="ReadPreloader32Async"/>, but the reply bytes are returned in wire order without
    /// re-packing each doubleword, so callers that need byte identity keep it exactly. 帧结构对应
    /// <c>length == 32</c>且 <c>direct=True</c>的 <c>read</c>：交互与 <see cref="ReadPreloader32Async"/>
    /// 相同，但回复字节按线路顺序原样返回、不重排双字，需要字节恒等的调用方可以精确保留。</remarks>
    /// <param name="address">The first doubleword address; words walk upward by four bytes.
    /// <para>起始双字地址；字按四字节向上递增。</para></param>
    /// <param name="dwordCount">The positive doubleword count, limited so one exchange stays within
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>bytes. 正数双字数，其上限保证单次交换不超过
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>字节。</param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The payload bytes exactly as the wire delivered them.
    /// <para>与线路交付一致的载荷字节。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The doubleword count is not positive or the window leaves the 32-bit address space; no byte was sent.
    /// <para>双字数非正，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">An echo, status word or the payload was invalid; the session faults.
    /// <para>回显、状态字或载荷无效；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> ReadPreloader32DirectAsync(uint address, int dwordCount,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dwordCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dwordCount,
            MtkDaMemoryAccess.MaximumWordLoopReadBytes / MtkDaMemoryAccess.RegisterCellBytes);
        RequireDaMemoryWindow(address, checked(dwordCount * MtkDaMemoryAccess.RegisterCellBytes));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var payload = await ReadPreloaderWordsOwnedAsync(PreloaderRead32Command, address, dwordCount,
                    MtkDaMemoryAccess.RegisterCellBytes, cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader READ32_DIRECT 0x{address:X8} x{dwordCount}.");
                return payload;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Pushes one partition data blob to a preloader running as DA through command 0x70.
    /// <para>通过命令 0x70 把一个分区数据块推送到作为 DA 运行的 preloader。</para></summary>
    /// <remarks>
    /// The framing mirrors <c>send_partition_data</c>:
    /// the opcode is echoed, the UTF-8 partition
    /// name is truncated or null-padded to exactly 64 bytes and written without an echo check, the
    /// big-endian payload length is written, one status word must arrive at or below <c>0x00FF</c>
    /// before any payload byte, the payload streams in 0x200-byte chunks, and the exchange closes
    /// with the big-endian xflash checksum — the little-endian doubleword sum with any trailing bytes
    /// added individually.
    /// This path adds the trailing bytes with a loop bound of <c>4 - (len % 4)</c>, which
    /// over- or under-adds for lengths that are not two-mod-four; this path adds each remaining byte
    /// once, which agrees exactly with the required framing for every four-aligned payload. This path
    /// writes the checksum and returns without a completion status, so this path does not read one
    /// either; the documented caller follows with the 0x71 jump-to-partition exchange.
    /// <para>帧结构对应 <c>send_partition_data</c>：
    /// 回显操作码后，把 UTF-8 分区名截断或用 NUL 补齐到恰好 64 字节并直接写出（不校验回显），写出大端载荷
    /// 长度，在发送任何载荷字节之前必须收到不超过 <c>0x00FF</c> 的状态字，载荷按 0x200 字节分块流出，最后
    /// 以大端 xflash 校验和收尾——即小端双字之和，剩余字节逐个相加。
    /// 本路径用 <c>4 - (len % 4)</c> 的循环上限追加剩余字节，对非模 4 余 2 的长度会多加或漏加；本路径
    /// 对每个剩余字节只加一次，对所有按四对齐的载荷与要求的帧结构完全一致。本路径写出校验和后直接返回、不读完成状态，
    /// 因此本路径同样不读；已记载的调用方随后执行 0x71 跳转分区交互。</para>
    /// </remarks>
    /// <param name="partitionName">The partition name; its UTF-8 encoding is truncated or padded to
    /// exactly 64 bytes. <para>分区名称；其 UTF-8 编码被截断或补齐到恰好 64 字节。</para></param>
    /// <param name="data">The payload to push, positive and bounded by the download-agent image
    /// ceiling. <para>要推送的载荷；须为正且不超过下载代理镜像上限。</para></param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the push.
    /// <para>授权本次推送的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the checksum was written.
    /// <para>在校验和写出后完成的任务。</para></returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> is empty.
    /// <para><paramref name="partitionName"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload is empty or exceeds the download-agent
    /// image ceiling. <para>载荷为空或超过下载代理镜像上限。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">An echo or the status was refused, or a write was
    /// incomplete; the session faults. <para>回显或状态被拒绝，或写入不完整；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task SendPreloaderPartitionDataAsync(string partitionName, ReadOnlyMemory<byte> data,
        bool confirmWrite, CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        ArgumentException.ThrowIfNullOrEmpty(partitionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(data.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, MaximumAgentLength);
        RequireDaMemoryWriteConfirmation(confirmWrite, "preloader");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await SendPreloaderPartitionDataOwnedAsync(partitionName, data, cancellationToken)
                    .ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader send-partition-data {partitionName}: {data.Length} byte(s).");
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Runs the documented send-partition-data exchange after the caller confirmed the push.
    /// <para>在调用方确认推送后执行已记载的 send-partition-data 交互。</para></summary>
    /// <param name="partitionName">The validated partition name. 已校验的分区名称。</param>
    /// <param name="data">The validated payload. 已校验的载荷。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private async Task SendPreloaderPartitionDataOwnedAsync(string partitionName, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await EchoAsync([PreloaderSendPartitionDataCommand], cancellationToken).ConfigureAwait(false);
        await WriteAsync(EncodePreloaderPartitionName(partitionName), _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        var lengthField = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lengthField, (uint)data.Length);
        await WriteAsync(lengthField, _timeoutMs, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync("preloader partition data request", cancellationToken).ConfigureAwait(false);
        var payload = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(payload);
            for (var offset = 0; offset < data.Length; offset += PreloaderPartitionChunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteAsync(payload, offset, Math.Min(PreloaderPartitionChunkBytes, data.Length - offset),
                    _timeoutMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(payload);
        }

        var checksumField = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(checksumField, WordSum32LittleEndian.Compute(payload.AsSpan(0, data.Length)));
        await WriteAsync(checksumField, _timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the preloader watchdog policy: disable the watchdog only for a chip the
    /// port table does not mark as 64-bit and for which it records a sequence.
    /// <para>应用 preloader 初始化看门狗策略：仅对端口表未标记为 64 位、且记录了序列的芯片禁用看门狗。</para>
    /// </summary>
    /// <remarks>The preloader initialization performs this write after the hardware code is known and before
    /// the download-agent upload. Calling it from the bootstrap keeps
    /// the single <c>InitializeAsync</c> state machine intact instead of initializing the session twice.
    /// <para>preloader 初始化在硬件码已知之后、下载代理上传之前执行该写入。由引导流程调用它可以保持唯一的
    /// <c>InitializeAsync</c> 状态机，而不需要二次初始化会话。</para></remarks>
    /// <param name="device">The identity already queried from the target. 已从目标查询到的身份信息。</param>
    /// <param name="cancellationToken">The operation cancellation token. 操作取消令牌。</param>
    internal async Task DisablePreloaderWatchdogForBootRomAsync(MtkDeviceInfo device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (MtkPreloaderWatchdogTable.Is64BitHardware(device.HardwareCode)
            || MtkPreloaderWatchdogTable.Resolve(device.HardwareCode, device.HardwareVersion).Count == 0)
        {
            return;
        }

        await DisablePreloaderWatchdogAsync(confirmWrite: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disables the target watchdog with the chip-specific write sequence defined for that chip.
    /// <para>使用按芯片区分的已定义写序列禁用目标看门狗。</para></summary>
    /// <remarks>The steps come from <see cref="MtkPreloaderWatchdogTable.Resolve"/>; chips the table
    /// leaves without a sequence are refused with <see cref="NotSupportedException"/> naming the
    /// hardware code rather than being driven with invented registers. See the class remarks for the
    /// branch sources.
    /// <para>步骤来自 <see cref="MtkPreloaderWatchdogTable.Resolve"/>；芯片表未记录序列的芯片会以指名硬件码的
    /// <see cref="NotSupportedException"/> 被拒绝，而不是用凭空编造的寄存器去驱动。分支来源见类备注。</para></remarks>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the writes.
    /// <para>授权这些写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes after every documented step was acknowledged.
    /// <para>在每个已记载步骤均被确认后完成的任务。</para></returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="NotSupportedException">The port table records no disable sequence for the queried hardware code; no byte was sent.
    /// <para>端口表没有为查询到的硬件码记录禁用序列；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage or the identity was never queried.
    /// <para>会话不处于 preloader 命令阶段，或从未查询身份。</para></exception>
    /// <exception cref="MtkProtocolException">A step was refused; the session faults.
    /// <para>某一步被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task DisablePreloaderWatchdogAsync(bool confirmWrite,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        var device = RequireQueriedIdentity();
        var rules = MtkPreloaderWatchdogTable.Resolve(device.HardwareCode, device.HardwareVersion);
        if (rules.Count == 0)
        {
            throw new NotSupportedException(
                $"The reference watchdog table records no disable sequence for MediaTek hardware code 0x{device.HardwareCode:X4}; refusing to invent register writes.");
        }

        RequireDaMemoryWriteConfirmation(confirmWrite, "preloader");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                foreach (var rule in rules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (rule.Operation)
                    {
                        case MtkPreloaderWatchdogOperation.Write16:
                            await WritePreloader16OwnedAsync(rule.Address, new ushort[] { (ushort)rule.Value }, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        case MtkPreloaderWatchdogOperation.Write32:
                            await WritePreloader32OwnedAsync(rule.Address, new uint[] { rule.Value }, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        default:
                            var current = await ReadPreloaderWordsOwnedAsync(PreloaderRead32Command, rule.Address,
                                1, MtkDaMemoryAccess.RegisterCellBytes, cancellationToken).ConfigureAwait(false);
                            await WritePreloader32OwnedAsync(rule.Address,
                                new uint[] { BinaryPrimitives.ReadUInt32BigEndian(current) | rule.Value }, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                    }
                }

                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader watchdog disabled for 0x{device.HardwareCode:X4} in {rules.Count} step(s).");
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Arms the watchdog reset register so the next timeout reboots straight into the BROM.
    /// <para>武装看门狗复位寄存器，使下一次超时直接重启进入 BROM。</para></summary>
    /// <remarks>
    /// <c>reset_to_brom</c> composes the USBDL
    /// value from the timeout (scaled to seconds, shifted into the <c>0x0000FFFC</c> field, with the
    /// enable bit <c>0x1</c> optional, the bootloader bit <c>0x2</c> cleared so the BROM owns the
    /// download flag, and the magic <c>0x444C0000</c> applied) and then unlocks the
    /// <c>misc_lock</c> register with <c>0xAD98</c>, marks the watchdog-resettable control at
    /// <c>misc_lock + 8</c>, relocks with <c>0</c>, and finally writes the USBDL value to
    /// <c>misc_lock - 0x20</c>. A zero timeout selects the no-timeout maximum. The unlock
    /// sequence writes device state, so it is only attempted when the caller confirms it.
    /// <c>reset_to_brom</c>组合 USBDL 值：超时按秒缩放、
    /// 移入 <c>0x0000FFFC</c> 字段、可选置位使能位 <c>0x1</c>、清除 bootloader 位 <c>0x2</c> 以让 BROM
    /// 持有下载标志、并加上魔数 <c>0x444C0000</c>；随后用 <c>0xAD98</c> 解锁 <c>misc_lock</c> 寄存器、把
    /// 看门狗可复位控制写入 <c>misc_lock + 8</c>、以 <c>0</c> 重新加锁，最后把 USBDL 值写入
    /// <c>misc_lock - 0x20</c>。超时为 0 时选择无超时最大值。解锁序列会写入设备状态，因此仅在
    /// 调用方确认后才发起。
    /// </remarks>
    /// <param name="enable">Whether the download-enable bit is set; the default is
    /// <see langword="true"/>. <para>是否置位下载使能位；默认为 <see langword="true"/>。</para></param>
    /// <param name="timeoutMs">The watchdog timeout budget in milliseconds before the reset reboots
    /// into the BROM; <c>0</c> selects the no-timeout maximum.
    /// <para>超时重启回到 BROM 前的看门狗毫秒预算；<c>0</c> 选择无超时最大值。</para></param>
    /// <param name="confirm">The explicit caller opt-in that authorizes the reset arming.
    /// <para>授权武装复位的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after the USBDL flag word was acknowledged.
    /// <para>在 USBDL 标志字被确认后完成的任务。</para></returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirm"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is negative.
    /// <para><paramref name="timeoutMs"/> 为负。</para></exception>
    /// <exception cref="NotSupportedException">The port table records no <c>misc_lock</c>
    /// register for the queried hardware code; no byte was sent.
    /// <para>端口表没有为查询到的硬件码记录 <c>misc_lock</c> 寄存器；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage or
    /// the identity was never queried. <para>会话不处于 preloader 命令阶段，或从未查询身份。</para></exception>
    /// <exception cref="MtkProtocolException">A step was refused; the session faults.
    /// <para>某一步被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task ReturnPreloaderToBootRomAsync(bool enable, int timeoutMs, bool confirm,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        var device = RequireQueriedIdentity();
        if (!MtkPreloaderWatchdogTable.TryResolveResetRegisters(device.HardwareCode, out var miscLock))
        {
            throw new NotSupportedException(
                $"The reference chip table records no misc_lock reset register for MediaTek hardware code 0x{device.HardwareCode:X4}; the return-to-BROM request was refused.");
        }

        RequireDaMemoryWriteConfirmation(confirm, "preloader");
        var usbdlRegister = ComputeUsbdlRegister(enable, timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await WritePreloader32OwnedAsync(miscLock, new uint[] { MiscLockKeyMagic }, cancellationToken)
                    .ConfigureAwait(false);
                await WritePreloader32OwnedAsync(checked(miscLock + 8), new uint[] { 1 }, cancellationToken)
                    .ConfigureAwait(false);
                await WritePreloader32OwnedAsync(miscLock, new uint[] { 0 }, cancellationToken).ConfigureAwait(false);
                await WritePreloader32OwnedAsync(checked(miscLock - 0x20), new uint[] { usbdlRegister }, cancellationToken)
                    .ConfigureAwait(false);
                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader armed reset-to-BROM: misc_lock 0x{miscLock:X8}, USBDL 0x{usbdlRegister:X8}.");
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads a bounded memory window through chunked big-endian thirty-two-bit reads and re-emits it little-endian.
    /// <para>通过分块的大端三十二位读取读取有界内存窗口，并按小端重新输出。</para></summary>
    /// <remarks>The preloader <c>peek</c> handler requests at most 512 bytes per READ32 command,
    /// decodes the big-endian reply, and re-packs each doubleword little-endian; the tail is rounded up
    /// to a whole word on the wire and trimmed afterwards, exactly like the Legacy DA reader. A refused
    /// or short chunk is a protocol fault here rather than an empty or short result. preloader 的
    /// <c>peek</c> 处理程序每条 READ32 命令最多请求 512
    /// 字节，解码大端回复后把每个双字按小端重新打包；线路上的尾部向上取整到完整双字并在 之后裁剪，与 Legacy DA 读取器一致。被拒绝或过短的分块在这里是协议错误，而不是空或短结果。</remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> little-endian bytes.
    /// <para>恰好 <paramref name="length"/> 字节的小端内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves the 32-bit address space; no byte was sent.
    /// <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk was refused, malformed, or short; the session faults.
    /// <para>某分块被拒绝、格式错误或过短；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> PeekPreloaderMemoryAsync(uint address, int length,
        CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        MtkDaMemoryAccess.ValidateWordLoopReadLength(length);
        RequireDaMemoryWindow(address, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var data = new byte[length];
                var position = 0;
                while (position < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunkBytes = Math.Min(PreloaderPeekChunkBytes, length - position);
                    var payload = await ReadPreloaderWordsOwnedAsync(PreloaderRead32Command,
                        checked(address + (uint)position), MtkDaMemoryAccess.ComputeWordCount(chunkBytes),
                        MtkDaMemoryAccess.RegisterCellBytes, cancellationToken).ConfigureAwait(false);
                    for (var word = 0; word * MtkDaMemoryAccess.RegisterCellBytes < chunkBytes; word++)
                    {
                        var value = BinaryPrimitives.ReadUInt32BigEndian(
                            payload.AsSpan(word * MtkDaMemoryAccess.RegisterCellBytes));
                        var cell = new byte[MtkDaMemoryAccess.RegisterCellBytes];
                        BinaryPrimitives.WriteUInt32LittleEndian(cell, value);
                        cell.AsSpan(0,
                            Math.Min(MtkDaMemoryAccess.RegisterCellBytes, chunkBytes - (word * MtkDaMemoryAccess.RegisterCellBytes)))
                            .CopyTo(data.AsSpan(position + (word * MtkDaMemoryAccess.RegisterCellBytes)));
                    }

                    position += chunkBytes;
                }

                RecordSecurityEvidence("preloader-command", CommandOutcomeKind.Completed,
                    $"preloader peek 0x{address:X8}+0x{length:X}.");
                return data;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Asserts the session sits in the preloader command stage before any command byte.
    /// <para>在任何命令字节之前断言会话处于 preloader 命令阶段。</para></summary>
    /// <remarks>
    /// The command stage is the bootloader stage reached by <c>InitializeAsync</c> before any
    /// download-agent load request began; once <see cref="MtkDaUploadStage.AgentRequest"/> is entered
    /// the same transport belongs to the XFlash, XML or Legacy bring-up and preloader command bytes
    /// must not interleave with it.
    /// 命令阶段即 <c>InitializeAsync</c> 达成、且在下载代理加载请求开始之前的 bootloader 阶段；一旦进入
    /// <see cref="MtkDaUploadStage.AgentRequest"/>，同一传输就属于 XFlash、XML 或 Legacy 启动流程，preloader
    /// 命令字节不得与其交错。
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transport is disconnected, the session left the
    /// bootloader stage, or a download-agent upload has begun.
    /// <para>传输已断开、会话离开 bootloader 阶段，或下载代理上传已开始。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    private void RequirePreloaderCommandStage()
    {
        ThrowIfDisposed();
        if (!Transport.IsConnected || _state != MtkSessionState.Bootloader
            || _uploadStage >= MtkDaUploadStage.AgentRequest)
        {
            throw new InvalidOperationException(
                $"Preloader command access requires an open transport in state Bootloader before any download-agent upload; " +
                $"current state is {_state} at upload stage {_uploadStage}.");
        }
    }

    /// <summary>Moves the session to the faulted stage after a refused command-stage exchange.
    /// <para>在被拒绝的命令阶段交换之后把会话移入 fault 阶段。</para></summary>
    private void FaultPreloaderCommands() => _state = MtkSessionState.Faulted;

    private MtkDeviceInfo RequireQueriedIdentity() => DeviceInfo ?? throw new InvalidOperationException(
        "Query the boot identity before addressing chip-specific preloader registers.");

    private static void RequireDaMemoryWriteConfirmation(bool confirmWrite, string family)
    {
        if (!confirmWrite)
        {
            throw new UnauthorizedAccessException(
                $"The {family} download agent write changes device memory and requires an explicit confirmation.");
        }
    }

    private static void RequireDaMemoryWindow(uint address, int lengthBytes)
    {
        if ((ulong)address + (ulong)(uint)lengthBytes > 0x1_0000_0000UL)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address,
                "The requested range leaves the 32-bit download-agent address space.");
        }

        if (MtkDaMemoryAccess.IntersectsEfuseWindow(address, (long)address + lengthBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(address), address,
                "Ordinary download-agent memory access does not reach the eFuse window; capture it as an " +
                "explicit eFuse region instead.");
        }
    }

    /// <summary>Runs one documented READ exchange and returns the raw big-endian payload.
    /// <para>执行一次已记载的 READ 交换并返回原始大端载荷。</para></summary>
    /// <param name="command">The READ opcode. READ 操作码。</param>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="words">The word count announced to the device. 向设备声明的字数。</param>
    /// <param name="wordBytes">The width of one word. 单字宽度。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The payload bytes exactly as delivered. 按交付原样返回的载荷字节。</returns>
    private async Task<byte[]> ReadPreloaderWordsOwnedAsync(byte command, uint address, int words, int wordBytes,
        CancellationToken cancellationToken)
    {
        await EchoAsync([command], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(address, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async((uint)words, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync("preloader read request", cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync(checked(words * wordBytes), _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        await ReadStatusAsync("preloader read completion", cancellationToken).ConfigureAwait(false);
        return payload;
    }

    /// <summary>Runs one documented WRITE16 exchange after the caller confirmed the write.
    /// <para>在调用方确认写入后执行一次已记载的 WRITE16 交换。</para></summary>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="values">The big-endian-encoded words. 按大端编码的字。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private async Task WritePreloader16OwnedAsync(uint address, ReadOnlyMemory<ushort> values,
        CancellationToken cancellationToken)
    {
        await EchoAsync([PreloaderWrite16Command], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(address, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async((uint)values.Length, cancellationToken).ConfigureAwait(false);
        RequirePreloaderWriteAccepted(BinaryPrimitives.ReadUInt16BigEndian(
            await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false)));
        var frame = new byte[sizeof(ushort)];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame, values.Span[index]);
            await EchoAsync((byte[])frame.Clone(), cancellationToken).ConfigureAwait(false);
        }

        await ReadStatusAsync("preloader 16-bit write completion", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one documented WRITE32 exchange after the caller confirmed the write.
    /// <para>在调用方确认写入后执行一次已记载的 WRITE32 交换。</para></summary>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="values">The big-endian-encoded doublewords. 按大端编码的双字。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private async Task WritePreloader32OwnedAsync(uint address, ReadOnlyMemory<uint> values,
        CancellationToken cancellationToken)
    {
        await EchoAsync([PreloaderWrite32Command], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(address, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async((uint)values.Length, cancellationToken).ConfigureAwait(false);
        RequirePreloaderWriteAccepted(BinaryPrimitives.ReadUInt16BigEndian(
            await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false)));
        for (var index = 0; index < values.Length; index++)
        {
            await EchoUInt32Async(values.Span[index], cancellationToken).ConfigureAwait(false);
        }

        await ReadStatusAsync("preloader 32-bit write completion", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the write gate: values may only follow an opening status at or below
    /// <c>0x0003</c>. <para>应用写入门：只有在首个状态不超过 <c>0x0003</c> 之后才允许发送值。</para></summary>
    /// <param name="status">The opening status word. 首个状态字。</param>
    /// <exception cref="MtkProtocolException">The status is a hard error or the value-window
    /// status, which sends no value bytes. <para>状态为硬错误，或是值窗口状态，此时不发送任何值字节。</para></exception>
    private static void RequirePreloaderWriteAccepted(ushort status)
    {
        if (status > PreloaderWriteAcceptedStatus)
        {
            throw new MtkProtocolException(
                $"The preloader write was refused before any value byte (status 0x{status:X4}).", status);
        }
    }

    /// <summary>Truncates or null-pads a partition name to the exact 64-byte wire field.
    /// <para>把分区名称截断或用 NUL 补齐到精确的 64 字节线路字段。</para></summary>
    /// <param name="partitionName">The non-empty partition name. 非空分区名称。</param>
    /// <returns>Exactly <see cref="PreloaderPartitionNameBytes"/> bytes.
    /// <para>恰好 <see cref="PreloaderPartitionNameBytes"/> 字节。</para></returns>
    private static byte[] EncodePreloaderPartitionName(string partitionName)
    {
        var encoded = Encoding.UTF8.GetBytes(partitionName);
        var field = new byte[PreloaderPartitionNameBytes];
        encoded.AsSpan(0, Math.Min(PreloaderPartitionNameBytes, encoded.Length)).CopyTo(field);
        return field;
    }

    /// <summary>Composes the USBDL flag word of the reset-to-BROM sequence.
    /// <para>组合 reset-to-BROM 序列的 USBDL 标志字。</para></summary>
    /// <param name="enable">Whether the download-enable bit is set. 是否置位下载使能位。</param>
    /// <param name="timeoutMs">The timeout in milliseconds; <c>0</c> selects the no-timeout maximum.
    /// <para>毫秒超时；<c>0</c> 选择无超时最大值。</para></param>
    /// <returns>The flag word to store in the USBDL register. 要存入 USBDL 寄存器的标志字。</returns>
    private static uint ComputeUsbdlRegister(bool enable, int timeoutMs)
    {
        var seconds = timeoutMs == 0 ? UsbdlTimeoutMax : (uint)(timeoutMs / 1000);
        var register = (seconds << 2) & UsbdlTimeoutMask;
        if (enable)
        {
            register |= UsbdlBitEnable;
        }
        else
        {
            register &= ~UsbdlBitEnable;
        }

        register &= ~UsbdlBitBootLoader;
        register |= UsbdlMagic;
        return register;
    }

}