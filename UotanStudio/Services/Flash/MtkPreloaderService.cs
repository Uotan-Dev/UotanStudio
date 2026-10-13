using UotanStudio.Comm.Protocols.Mtk;

namespace UotanStudio.Services.Flash;

/// <summary>Exposes the MediaTek preloader command-stage register, memory and reset paths behind
/// confirmation gates.
/// <para>在确认门之后暴露 MediaTek preloader 命令阶段的寄存器、内存与复位路径。</para></summary>
internal static class MtkPreloaderService
{
    /// <summary>Reads a bounded preloader memory window and returns it exactly as the reference peek
    /// re-emits it. <para>读取有界的 preloader 内存窗口，并按参考 peek 的输出方式原样返回。</para></summary>
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="address">The start address of the window.<para>窗口起始地址。</para></param>
    /// <param name="length">The positive byte count for a single word-loop read.
    /// <para>单次按字循环读取的正数字节数。</para></param>
    /// <param name="ct">Cancellation honoured at every cooperative point.
    /// <para>在每处协作点都会响应的取消令牌。</para></param>
    /// <returns>The window bytes as the reference peek emits them.<para>参考 peek 输出的窗口字节。</para></returns>
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
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="address">The first byte address; it must be 32-bit aligned.
    /// <para>起始字节地址；必须按 32 位对齐。</para></param>
    /// <param name="data">The payload bytes to write; a non-multiple of four pads up to three trailing bytes.
    /// <para>要写入的载荷字节；非四倍数长度会补齐最多三个尾部字节。</para></param>
    /// <param name="confirmed">The explicit opt-in that authorizes every write byte.
    /// <para>授权所有写入字节的显式确认。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every chunk.
    /// <para>在首个设备字节之前以及每块之间都会响应的取消令牌。</para></param>
    /// <returns>The number of payload bytes submitted to the device.<para>已提交给设备的载荷字节数。</para></returns>
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
    /// <param name="session">A session sitting in the preloader command stage with a queried identity.
    /// <para>处于 preloader 命令阶段且已查询身份的会话。</para></param>
    /// <param name="timeoutSeconds">The watchdog budget in seconds before the reset reboot; <c>0</c> selects
    /// the reference no-timeout maximum. <para>武装复启前的看门狗秒数预算；<c>0</c> 选择参考的无超时最大值。</para></param>
    /// <param name="confirmed">The explicit opt-in that authorizes the reset arming.
    /// <para>授权武装复位的显式确认。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
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
    /// <param name="session">A session sitting in the preloader command stage with a queried identity.
    /// <para>处于 preloader 命令阶段且已查询身份的会话。</para></param>
    /// <param name="confirmed">The explicit opt-in that authorizes the watchdog writes.
    /// <para>授权看门狗写入的显式确认。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
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
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="partitionName">The partition to boot; non-empty, encoded by the session.
    /// <para>要引导的分区；非空，由会话编码。</para></param>
    /// <param name="confirmed">The explicit opt-in that authorizes the jump.
    /// <para>授权本次跳转的显式确认。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
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

    /// <summary>Asks a preloader running as download agent to hand control over to its bootloader stage.
    /// <para>请求作为下载代理运行的 preloader 把控制权交给其 bootloader 阶段。</para></summary>
    /// <param name="session">A session sitting in the preloader command stage.
    /// <para>处于 preloader 命令阶段的会话。</para></param>
    /// <param name="confirmed">The explicit opt-in that authorizes the jump.
    /// <para>授权本次跳转的显式确认。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte.
    /// <para>在首个设备字节之前会响应的取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmed"/> is false; no byte was sent.
    /// <para><paramref name="confirmed"/> 为 false；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The status handshake was refused; the session faults.
    /// <para>状态握手被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    internal static Task JumpToBootLoaderAsync(MtkSession session, bool confirmed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireConfirmation(confirmed, "preloader jump-to-boot-loader");
        return session.JumpPreloaderToBootLoaderAsync(confirm: true, ct);
    }

    /// <summary>Refuses a destructive preloader action before the session is touched when it is not
    /// confirmed. <para>在未获确认时，于触碰会话之前拒绝破坏性的 preloader 操作。</para></summary>
    /// <param name="confirmed">The explicit opt-in authorizing the action.<para>授权该操作的显式确认。</para></param>
    /// <param name="action">The action name used in the refusal message.<para>用于拒绝提示文案的操作名称。</para></param>
    private static void RequireConfirmation(bool confirmed, string action)
    {
        if (!confirmed)
        {
            throw new UnauthorizedAccessException(
                $"The {action} changes device state and requires an explicit confirmation.");
        }
    }

    /// <summary>Rejects a window whose byte range would leave the 32-bit address space before any byte
    /// is sent. <para>在发送任何字节之前，拒绝其字节范围会越出 32 位地址空间的窗口。</para></summary>
    /// <param name="address">The window start address.<para>窗口的起始地址。</para></param>
    /// <param name="length">The window byte count.<para>窗口的字节数量。</para></param>
    private static void RequireWindowWithinAddressSpace(uint address, int length)
    {
        var end = checked((long)address + length);
        if (end > uint.MaxValue + 1L)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                $"The window 0x{address:X8}+0x{length:X} leaves the 32-bit preloader address space.");
        }
    }
}