using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>Exposes the MediaTek preloader command-stage register, memory and reset paths to the flash
/// page behind confirmation gates and atomic file publication.
/// <para>在确认门与原子文件发布之后，把 MediaTek preloader 命令阶段的寄存器、内存与复位路径暴露给刷写页。</para></summary>
/// <remarks>
/// <para>This is the host counterpart of the <see cref="MtkSession"/> preloader command surface. Every
/// destructive path refuses an unconfirmed caller before it touches the session, so a missing opt-in never
/// opens the transport, checks the session stage, or sends a single device byte. Reads and dumps only drive
/// the passed-in session; the host never opens a socket, serial port or USB handle itself.</para>
/// <para>这是 <see cref="MtkSession"/> preloader 命令接口在宿主机侧的对应实现。每条破坏性路径都在触碰到会话
/// 之前拒绝未确认的调用方，因此缺少授权时绝不会打开传输、检查会话阶段或发送任何设备字节。读取与转储只驱动
/// 传入的会话；宿主从不自行打开套接字、串口或 USB 句柄。</para>
/// <para>The session faults on any protocol refusal, truncation or echo mismatch, and a faulted session
/// rejects every later command. Callers therefore must discard and rebuild the session after any failure
/// instead of retrying on the same instance; the faulted stage is terminal for the preloader command
/// surface.</para>
/// <para>会话在任何协议拒绝、截断或回显不匹配时进入 fault 状态，而 fault 后的会话会拒绝所有后续命令。因此
/// 调用方在任一失败后必须丢弃并重建会话，而不是在同一实例上重试；对 preloader 命令接口而言 fault 阶段是
/// 终止性的。</para>
/// </remarks>
internal static class MtkPreloaderService
{
    private const string InternalFlashRegionName = "IFLASH";

    /// <summary>Dumps a bounded internal-flash window to a file through the preloader command stage and
    /// publishes it atomically. <para>通过 preloader 命令阶段转储有界的内部闪存窗口并原子发布到文件。</para></summary>
    /// <remarks>
    /// <para>The window is walked one READ32 chunk at a time into a staging file named
    /// <c>&lt;destination&gt;.uotan-&lt;guid&gt;</c> while its bytes are hashed incrementally. The
    /// destination is replaced by an atomic move only after the staged byte count equals the requested
    /// length, so a protocol fault or cancellation at any step leaves any pre-existing image untouched and
    /// removes the staging file.</para>
    /// <para>窗口按每次一条 READ32 分块被遍历进名为 <c>&lt;目标&gt;.uotan-&lt;guid&gt;</c> 的暂存文件，同时增量
    /// 哈希其字节。只有暂存字节数与请求长度相等时，目标才被一次原子移动替换；因此任一步骤上的协议错误或取消
    /// 都会保留既有镜像并删除暂存文件。</para>
    /// <para><paramref name="confirmFlashMapping"/> authorizes only the MT6261 flash-over-RAM remap write;
    /// other chips ignore it and the session refuses the remap for MT6261 when it is false, before any
    /// device byte. If the session faults, the caller must rebuild it before issuing another command.</para>
    /// <para><paramref name="confirmFlashMapping"/> 仅授权 MT6261 的闪存覆盖 RAM 重映射写入；其它芯片忽略它，
    /// 而当其为 false 时，会话会在 MT6261 上于任何设备字节之前拒绝该重映射。若会话进入 fault，调用方必须在
    /// 发出下一条命令前重建会话。</para>
    /// </remarks>
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="offset">The inclusive start address of the window. 窗口起始（含）地址。</param>
    /// <param name="length">The positive byte count to dump. 要转储的正数字节数。</param>
    /// <param name="stepBytes">The chunk size per READ32 command, a positive multiple of four.
    /// <para>每条 READ32 命令的分块大小，须为四的正整数倍。</para></param>
    /// <param name="confirmFlashMapping">Required only for the MT6261 flash remap; ignored by other chips.
    /// <para>仅 MT6261 的闪存重映射需要；其它芯片忽略。</para></param>
    /// <param name="destinationPath">Destination file inside an existing directory; replaced only on
    /// success. <para>位于已存在目录中的目标文件；仅在成功时被替换。</para></param>
    /// <param name="progress">Optional per-step progress; full progress is not publication.
    /// <para>可选的按步进度；进度跑满并不等于发布成功。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every chunk.
    /// <para>在首个设备字节之前以及每块之间都会响应的取消令牌。</para></param>
    /// <returns>The published image description. 已发布镜像的描述信息。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is empty or whitespace.
    /// <para><paramref name="destinationPath"/> 为空或仅空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.
    /// <para>目标目录不存在。</para></exception>
    /// <exception cref="IOException">The staged length did not match the requested length, or publication
    /// failed. <para>暂存长度与请求长度不符，或发布失败。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The chip is MT6261 and
    /// <paramref name="confirmFlashMapping"/> is false; the destination stays untouched.
    /// <para>芯片为 MT6261 且 <paramref name="confirmFlashMapping"/> 为 false；目标保持不变。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk was refused, malformed, or short; the session faults.
    /// <para>某分块被拒绝、格式错误或过短；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point; the
    /// destination is left untouched. <para>在任一协作点请求了取消；目标保持不变。</para></exception>
    internal static async Task<MtkPreloaderDumpRecord> DumpInternalFlashAsync(MtkSession session, uint offset,
        int length, int stepBytes, bool confirmFlashMapping, string destinationPath,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The dump destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The dump destination directory '{directory}' does not exist.");
        }

        ct.ThrowIfCancellationRequested();
        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        long captured;
        string sha256Hex;
        progress?.Report(new FlashProgress(InternalFlashRegionName, 0,
            $"Dumping 0x{length:X} bytes of internal flash from 0x{offset:X8} in {stepBytes:X}-byte steps..."));
        var stepProgress = progress is null
            ? null
            : new StepProgress(length, InternalFlashRegionName, progress);
        try
        {
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var hashingDestination = new HashingWriteStream(staging, leaveOpen: true);
                captured = await session
                    .DumpPreloaderInternalFlashAsync(hashingDestination, offset, length, stepBytes,
                        confirmFlashMapping, stepProgress, ct)
                    .ConfigureAwait(false);

                if (captured != length || staging.Length != length)
                {
                    throw new IOException(
                        $"The staged preloader internal-flash dump holds {staging.Length} bytes after a reported " +
                        $"{captured} but {length} were requested, so the destination was left untouched.");
                }

                sha256Hex = Convert.ToHexString(hashingDestination.GetHashAndReset());
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            DiscardStaging(stagingPath);
        }

        progress?.Report(new FlashProgress(InternalFlashRegionName, 100,
            $"Published {captured} bytes of the preloader internal-flash window with SHA-256 {sha256Hex}."));
        return new MtkPreloaderDumpRecord(InternalFlashRegionName, offset, length, stepBytes, captured,
            sha256Hex, destination);
    }

    /// <summary>Reads a bounded preloader memory window and returns it exactly as the reference peek
    /// re-emits it. <para>读取有界的 preloader 内存窗口，并按参考 peek 的输出方式原样返回。</para></summary>
    /// <remarks>
    /// The session already bounds the request to one word-loop read and faults on a refused or short
    /// chunk, so no confirmation is needed: the exchange reads device memory without changing it. A faulted
    /// session must be rebuilt before the next command.
    /// 会话已把请求限制在单次按字循环读取之内，并在被拒绝或过短的分块上进入 fault，因此无需确认：该交换读取
    /// 设备内存而不改变它。fault 后的会话必须在下一条命令前重建。
    /// </remarks>
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count for a single word-loop read.
    /// <para>单次按字循环读取的正数字节数。</para></param>
    /// <param name="ct">Cancellation honoured at every cooperative point.
    /// <para>在每处协作点都会响应的取消令牌。</para></param>
    /// <returns>The window bytes as the reference peek emits them. 参考 peek 输出的窗口字节。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves the
    /// 32-bit address space. <para>长度越界，或窗口越出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk was refused, malformed, or short; the session faults.
    /// <para>某分块被拒绝、格式错误或过短；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    internal static Task<byte[]> PeekWindowAsync(MtkSession session, uint address, int length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.PeekPreloaderMemoryAsync(address, length, ct);
    }

    /// <summary>Writes a byte block through the preloader write command, splitting payloads that exceed the
    /// session's per-command ceiling into aligned chunks.
    /// <para>通过 preloader 写入命令写入字节块，并把超过会话单命令上限的载荷拆成对齐的分块。</para></summary>
    /// <remarks>
    /// <para>When <paramref name="confirmed"/> is false the call throws before it touches the session, so
    /// the transport stays untouched and no window is validated against the device. Addresses are required
    /// to be 32-bit aligned up front; the chunk size keeps every address after the first one aligned
    /// because all intermediate chunks are exactly <see cref="MtkDaMemoryAccess.MaximumWordLoopWriteBytes"/>
    /// bytes.</para>
    /// <para>当 <paramref name="confirmed"/> 为 false 时，调用在触碰会话之前即抛出，因此传输保持不变，窗口也不会
    /// 对照设备进行校验。地址被要求预先按 32 位对齐；由于除末块外每个分块都恰为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopWriteBytes"/> 字节，首个地址之后的每个地址仍保持对齐。</para>
    /// <para>A session that faults mid-write stops accepting further commands; rebuild it before retrying.
    /// <para>某次写入中途进入 fault 的会话会停止接受后续命令；重试前请重建会话。</para></para>
    /// </remarks>
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="address">The first byte address; it must be 32-bit aligned.
    /// <para>起始字节地址；必须按 32 位对齐。</para></param>
    /// <param name="data">The payload; a length that is not a multiple of four pads up to three trailing
    /// bytes just like the session's word-loop write. <para>载荷；非四倍数长度会像会话的按字写入那样补齐最多三个尾部字节。</para></param>
    /// <param name="confirmed">The explicit caller opt-in that authorizes every write byte.
    /// <para>授权所有写入字节的显式调用方选择。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every chunk.
    /// <para>在首个设备字节之前以及每块之间都会响应的取消令牌。</para></param>
    /// <returns>The number of payload bytes submitted to the device. 已提交给设备的载荷字节数。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is false; no byte was sent.
    /// <para><paramref name="confirmed"/> 为 false；未发送任何字节。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty, or
    /// <paramref name="address"/> is not 32-bit aligned; no byte was sent.
    /// <para><paramref name="data"/> 为空，或 <paramref name="address"/> 未按 32 位对齐；未发送任何字节。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The window leaves the 32-bit address space.
    /// <para>窗口越出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">A chunk was refused; the session faults.
    /// <para>某分块被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    internal static async Task<int> WriteWindowAsync(MtkSession session, uint address, ReadOnlyMemory<byte> data,
        bool confirmed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireConfirmation(confirmed, "preloader window write");
        if (data.IsEmpty)
        {
            throw new ArgumentException("A preloader window write needs at least one byte.", nameof(data));
        }

        if (address % MtkDaMemoryAccess.RegisterCellBytes != 0)
        {
            throw new ArgumentException(
                $"The preloader write address 0x{address:X8} is not 32-bit aligned.", nameof(address));
        }

        RequireWindowWithinAddressSpace(address, data.Length);

        var ceiling = MtkDaMemoryAccess.MaximumWordLoopWriteBytes;
        var submitted = 0;
        for (var position = 0; position < data.Length; position += ceiling)
        {
            ct.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(ceiling, data.Length - position);
            submitted += await session
                .WritePreloaderMemoryAsync(checked(address + (uint)position),
                    data.Slice(position, chunkLength), confirmWrite: true, ct)
                .ConfigureAwait(false);
        }

        return submitted;
    }

    /// <summary>Arms the preloader watchdog reset so the next timeout reboots the device straight into the
    /// boot ROM. <para>武装 preloader 看门狗复位，使下一次超时直接把设备重启进 boot ROM。</para></summary>
    /// <remarks>
    /// <para>The reset register address is resolved by the session from the queried hardware code; the host
    /// deliberately does not accept a caller-supplied address because inventing one would program the wrong
    /// register. <paramref name="timeoutSeconds"/> is scaled to the session's millisecond budget, and the
    /// reference default download-enable bit is used.</para>
    /// <para>复位寄存器地址由会话根据查询到的硬件码解析；宿主有意不接受调用方提供的地址，因为凭空给出的地址
    /// 会编程错误的寄存器。<paramref name="timeoutSeconds"/> 被换算为会话的毫秒预算，并使用参考默认的下载使能位。</para>
    /// <para>The arming writes device state, so it is refused before the session is touched unless
    /// <paramref name="confirmed"/> is true. After a protocol fault the session must be rebuilt.</para>
    /// <para>武装操作会写入设备状态，因此除非 <paramref name="confirmed"/> 为 true，否则在触碰会话之前即被拒绝。
    /// 协议错误后会话必须重建。</para>
    /// </remarks>
    /// <param name="session">A session sitting in the preloader command stage with a queried identity.
    /// <para>处于 preloader 命令阶段且已查询身份的会话。</para></param>
    /// <param name="timeoutSeconds">The watchdog budget in seconds before the reset reboot; <c>0</c> selects
    /// the reference no-timeout maximum. <para>武装复启前的看门狗秒数预算；<c>0</c> 选择参考的无超时最大值。</para></param>
    /// <param name="confirmed">The explicit caller opt-in that authorizes the reset arming.
    /// <para>授权武装复位的显式调用方选择。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
    /// <returns>A task that completes once the session armed the reset register.
    /// <para>在会话武装好复位寄存器后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is false; no byte was sent.
    /// <para><paramref name="confirmed"/> 为 false；未发送任何字节。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutSeconds"/> is negative or its
    /// millisecond conversion overflows. <para><paramref name="timeoutSeconds"/> 为负，或其毫秒换算溢出。</para></exception>
    /// <exception cref="NotSupportedException">The reference chip table records no reset register for the
    /// queried hardware code. <para>参考芯片表没有为查询到的硬件码记录复位寄存器。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage or its
    /// identity was never queried. <para>会话不处于 preloader 命令阶段，或从未查询身份。</para></exception>
    /// <exception cref="MtkProtocolException">A step was refused; the session faults.
    /// <para>某一步被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    internal static Task ReturnToBootRomAsync(MtkSession session, int timeoutSeconds, bool confirmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireConfirmation(confirmed, "preloader return-to-boot-ROM");
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);
        return session.ReturnPreloaderToBootRomAsync(enable: true, checked(timeoutSeconds * 1000),
            confirm: true, ct);
    }

    /// <summary>Disables the target watchdog with the chip-specific write sequence the session defines.
    /// <para>使用会话定义的、按芯片区分的写序列禁用目标看门狗。</para></summary>
    /// <remarks>
    /// The write sequence changes device state, so it is refused before the session is touched unless
    /// <paramref name="confirmed"/> is true. Chips the reference table leaves without a sequence are refused
    /// by the session with <see cref="NotSupportedException"/>. After a protocol fault the session must be
    /// rebuilt. <para>写序列会改变设备状态，因此除非 <paramref name="confirmed"/> 为 true，否则在触碰会话之前
    /// 即被拒绝。参考表未记录序列的芯片会被会话以 <see cref="NotSupportedException"/> 拒绝。协议错误后会话必须重建。</para>
    /// </remarks>
    /// <param name="session">A session sitting in the preloader command stage with a queried identity.
    /// <para>处于 preloader 命令阶段且已查询身份的会话。</para></param>
    /// <param name="confirmed">The explicit caller opt-in that authorizes the watchdog writes.
    /// <para>授权看门狗写入的显式调用方选择。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
    /// <returns>A task that completes once every documented step was acknowledged.
    /// <para>在每个已记载步骤均被确认后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is false; no byte was sent.
    /// <para><paramref name="confirmed"/> 为 false；未发送任何字节。</para></exception>
    /// <exception cref="NotSupportedException">The reference table records no disable sequence for the
    /// queried hardware code. <para>参考表没有为查询到的硬件码记录禁用序列。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage or its
    /// identity was never queried. <para>会话不处于 preloader 命令阶段，或从未查询身份。</para></exception>
    /// <exception cref="MtkProtocolException">A step was refused; the session faults.
    /// <para>某一步被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    internal static Task DisableWatchdogAsync(MtkSession session, bool confirmed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireConfirmation(confirmed, "preloader watchdog disable");
        return session.DisablePreloaderWatchdogAsync(confirmWrite: true, ct);
    }

    /// <summary>Asks a preloader running as download agent to boot the named partition.
    /// <para>请求作为下载代理运行的 preloader 引导指定分区。</para></summary>
    /// <remarks>
    /// Booting a partition changes device state, so it is refused before the session is touched unless
    /// <paramref name="confirmed"/> is true. The partition transition is expected to drop the link, so the
    /// caller treats the transport as one-shot and rebuilds the session afterwards.
    /// 引导分区会改变设备状态，因此除非 <paramref name="confirmed"/> 为 true，否则在触碰会话之前即被拒绝。分区转换
    /// 预计会断开链路，因此调用方把该传输视为一次性并在其后重建会话。
    /// </remarks>
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="partitionName">The partition to boot; non-empty, encoded by the session.
    /// <para>要引导的分区；非空，由会话编码。</para></param>
    /// <param name="confirmed">The explicit caller opt-in that authorizes the jump.
    /// <para>授权本次跳转的显式调用方选择。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
    /// <returns>A task that completes once the session accepted the jump status.
    /// <para>在会话接受跳转状态后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is false; no byte was sent.
    /// <para><paramref name="confirmed"/> 为 false；未发送任何字节。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> is empty or whitespace.
    /// <para><paramref name="partitionName"/> 为空或仅空白。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The echo or status was refused; the session faults.
    /// <para>回显或状态被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    internal static Task JumpToPartitionAsync(MtkSession session, string partitionName, bool confirmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireConfirmation(confirmed, "preloader jump-to-partition");
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        return session.JumpPreloaderToPartitionAsync(partitionName, confirm: true, ct);
    }

    /// <summary>Refuses a destructive preloader action before the session is touched when the caller has not
    /// confirmed it. <para>在调用方未确认时，于触碰会话之前拒绝破坏性的 preloader 操作。</para></summary>
    private static void RequireConfirmation(bool confirmed, string action)
    {
        if (!confirmed)
        {
            throw new UnauthorizedAccessException(
                $"The {action} changes device state and requires an explicit confirmation.");
        }
    }

    /// <summary>Rejects a window whose byte range would leave the 32-bit address space before any byte
    /// leaves the host. <para>在任何字节离开主机之前拒绝其字节范围会越出 32 位地址空间的窗口。</para></summary>
    private static void RequireWindowWithinAddressSpace(uint address, int length)
    {
        var end = checked((long)address + length);
        if (end > uint.MaxValue + 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                $"The window 0x{address:X8}+0x{length:X} leaves the 32-bit preloader address space.");
        }
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

    /// <summary>Converts the session's cumulative byte count into a percentage progress message.
    /// <para>把会话累计的字节数转换为百分比进度消息。</para></summary>
    private sealed class StepProgress(int totalBytes, string regionName, IProgress<FlashProgress> sink)
        : IProgress<long>
    {
        public void Report(long value)
        {
            var percent = (int)Math.Clamp(Math.Round(value / (double)totalBytes * 99,
                MidpointRounding.AwayFromZero), 0, 99);
            sink.Report(new FlashProgress(regionName, percent, $"Captured {value} of {totalBytes} bytes..."));
        }
    }
}

/// <summary>Describes one atomically published preloader internal-flash dump.
/// <para>描述一次原子发布的 preloader 内部闪存转储。</para></summary>
/// <param name="RegionName">The window name used in progress reports.
/// <para>进度报告使用的窗口名称。</para></param>
/// <param name="StartOffset">The inclusive start address that was walked.
/// <para>实际遍历的起始（含）地址。</para></param>
/// <param name="RequestedBytes">The byte count the caller requested. <para>调用方请求的字节数。</para></param>
/// <param name="StepBytes">The address step one READ32 command covered.
/// <para>单条 READ32 命令覆盖的地址步长。</para></param>
/// <param name="ByteLength">The published byte count. <para>已发布的字节数。</para></param>
/// <param name="Sha256Hex">The upper-case SHA-256 of the published bytes.
/// <para>已发布字节的大写 SHA-256。</para></param>
/// <param name="DestinationPath">The absolute published path. <para>已发布的绝对路径。</para></param>
internal sealed record MtkPreloaderDumpRecord(string RegionName, uint StartOffset, int RequestedBytes,
    int StepBytes, long ByteLength, string Sha256Hex, string DestinationPath);