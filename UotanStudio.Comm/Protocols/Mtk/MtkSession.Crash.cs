using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>The three download-agent crash exchanges the reference client uses to force a stuck
/// preloader back to Boot ROM.
/// <para>参考客户端用于把卡死的 preloader 强制打回 Boot ROM 的三种下载代理 crash 交互。</para></summary>
/// <remarks>
/// The enumeration values are the reference <c>mode</c> numbers of
/// <c>crash(self, mode=0)</c> in Library/exploit_handler.py:146-163; the dispatcher is the
/// <c>crash</c> branch of Library/mtk_main.py:532-536 which calls <c>mtk.crasher(mode=...)</c>
/// after <c>preloader.init()</c>. Reference mode 3 delegates to <c>reset_to_brom(en=True)</c>;
/// that is a reboot, not a crash, and this session already offers dedicated reboot and shutdown
/// commands, so no crash mode maps to it.
/// <para>枚举取值即 Library/exploit_handler.py:146-163 中 <c>crash(self, mode=0)</c> 的参考
/// <c>mode</c> 编号；分发点是 Library/mtk_main.py:532-536 的 <c>crash</c> 分支，它在
/// <c>preloader.init()</c> 之后调用 <c>mtk.crasher(mode=...)</c>。参考模式 3 委托给
/// <c>reset_to_brom(en=True)</c>；那是重启而不是 crash，本会话已有专用的重启与关闭命令，因此没有
/// crash 模式映射到它。</para>
/// </remarks>
public enum MtkDaCrashMode
{
    /// <summary>Send a DA-upload frame whose declared data size equals the signature length so the
    /// agent's signature handling overruns. Reference mode 0 (<c>send_da(0, 0x100, 0x100,
    /// b'\x00' * 0x100)</c>, Library/exploit_handler.py:150).
    /// <para>发送一条声明数据大小等于签名长度的 DA 上传帧，使代理的签名处理越界。参考模式 0
    /// （<c>send_da(0, 0x100, 0x100, b'\x00' * 0x100)</c>，Library/exploit_handler.py:150）。</para>
    /// </summary>
    Dasend1 = 0,
    /// <summary>Read 0x100 doublewords from address 0 so the stuck agent dies inside its read loop.
    /// Reference mode 1 (<c>read32(0, 0x100)</c>, Library/exploit_handler.py:152).
    /// <para>从地址 0 读取 0x100 个双字，使卡死的代理在其读取循环中崩溃。参考模式 1
    /// （<c>read32(0, 0x100)</c>，Library/exploit_handler.py:152）。</para></summary>
    Daread = 1,
    /// <summary>Upload an ARM sequence whose second instruction is undefined and jump to it.
    /// Reference mode 2 (<c>send_da(0x0, len(payload), 0x0, payload)</c> then <c>jump_da(0x0)</c>,
    /// Library/exploit_handler.py:154-156).
    /// <para>上传一段第二条指令为未定义指令的 ARM 序列并跳转执行。参考模式 2
    /// （<c>send_da(0x0, len(payload), 0x0, payload)</c> 后 <c>jump_da(0x0)</c>，
    /// Library/exploit_handler.py:154-156）。</para></summary>
    Dasend2 = 2
}

/// <summary>Reports what the target answered after a crash exchange was submitted.
/// <para>报告提交 crash 交互后目标的应答情况。</para></summary>
/// <param name="Mode">The crash mode that was submitted. 已提交的 crash 模式。</param>
/// <param name="TrailingExchangeCompleted"><see langword="true"/> when the target answered the
/// trailing status exchange after the triggering frame; <see langword="false"/> when it went
/// silent, which is the expected result of a successful crash.
/// <para>目标在触发帧之后应答了尾部状态交换时为 <see langword="true"/>；陷入静默时为
/// <see langword="false"/>，即成功 crash 的预期结果。</para></param>
/// <param name="LastStatusCode">The last status word the target returned, when it returned one.
/// <para>目标返回的最后一个状态字；未返回时为 null。</para></param>
/// <param name="Detail">A localization-neutral summary of the exchange.
/// <para>与本地化无关的交互摘要。</para></param>
public sealed record MtkDaCrashResult(MtkDaCrashMode Mode, bool TrailingExchangeCompleted,
    ushort? LastStatusCode, string Detail);

public sealed partial class MtkSession
{
    private const byte DaCrashSendCommand = 0xD7;
    private const byte DaCrashReadCommand = 0xD1;
    private const byte DaCrashJumpCommand = 0xD5;
    private const uint DaCrashDasend1Bytes = 0x100;
    private const uint DaCrashDareadWords = 0x100;
    private const uint DaCrashDasend2Bytes = 0x118;

    /// <summary>Submits one documented download-agent crash so a stuck preloader falls back to
    /// Boot ROM.
    /// <para>提交一条已记载的下载代理 crash，使卡死的 preloader 回落到 Boot ROM。</para></summary>
    /// <remarks>
    /// The frames reproduce <c>crash(self, mode=0)</c> of Library/exploit_handler.py:146-163 on the
    /// boot command channel the session already uses for the agent upload and jump:
    /// <c>Dasend1</c> mirrors <c>send_da(0, 0x100, 0x100, b'\x00' * 0x100)</c> with the same
    /// echo-verified 0xD7 command, big-endian address, size and signature-length words the upload
    /// path uses (Library/mtk_preloader.py:1090-1107), <c>Daread</c> mirrors
    /// <c>read32(0, 0x100)</c> with the 0xD1 echo triple and bounded drain
    /// (Library/mtk_preloader.py:388-406), and <c>Dasend2</c> uploads the eight-byte ARM sequence
    /// <c>00 01 9F E5 10 FF 2F E1</c> padded to 0x118 bytes with a zero signature length and then
    /// issues the 0xD5 jump to address 0 (Library/mtk_preloader.py:708-735). Unlike the reference,
    /// which wraps every mode in <c>except Exception: pass</c>, this path keeps the command echoes
    /// and the first status word strict and treats only the trailing exchange after the triggering
    /// frame as opportunistic, because a crashed agent by definition stops answering. The
    /// verification that the target really returned to Boot ROM is a reconnect and handshake, so
    /// after the exchange is submitted — whatever the target answered — the session faults and the
    /// caller must open a new one. Reference mode 3 (<c>reset_to_brom</c>) is a reboot rather than a
    /// crash and is deliberately not offered here; the existing shutdown and reboot commands cover
    /// it.
    /// <para>这些帧在会话已用于代理上传与跳转的 boot 命令通道上复现
    /// Library/exploit_handler.py:146-163 的 <c>crash(self, mode=0)</c>：<c>Dasend1</c> 与
    /// <c>send_da(0, 0x100, 0x100, b'\x00' * 0x100)</c> 一致，使用上传路径同样的 echo 校验
    /// 0xD7 命令与大端地址、大小、签名长度字（Library/mtk_preloader.py:1090-1107）；<c>Daread</c>
    /// 与 <c>read32(0, 0x100)</c> 一致，使用 0xD1 echo 三元组与有界排空
    /// （Library/mtk_preloader.py:388-406）；<c>Dasend2</c> 上传八字节 ARM 序列
    /// <c>00 01 9F E5 10 FF 2F E1</c> 并补零到 0x118 字节，以签名长度 0 提交后发出跳转到地址 0
    /// 的 0xD5 命令（Library/mtk_preloader.py:708-735）。与把所有模式包在
    /// <c>except Exception: pass</c> 里的参考不同：本路径保持命令 echo 与第一个状态字严格，只把触发帧
    /// 之后的尾部交换视为尽力而为，因为被成功打崩的代理按定义不再应答。目标是否真的回到 Boot ROM 只能由
    /// 重连与握手验证，因此无论目标答了什么，交互一经提交本会话就进入 fault 状态，调用方必须重新建立会话。
    /// 参考模式 3（<c>reset_to_brom</c>）是重启而非 crash，此处有意不提供；现有的关闭与重启命令已覆盖
    /// 该场景。</para>
    /// </remarks>
    /// <param name="mode">The crash exchange to submit. 要提交的 crash 交互。</param>
    /// <param name="confirm">The explicit caller opt-in that authorizes interrupting the boot
    /// channel. <para>授权中断 boot 通道的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A result naming the mode, whether the target answered the trailing exchange and the
    /// last observed status word. <para>写明模式、目标是否应答尾部交换与最后观测状态字的结果。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not one of the three
    /// documented crash modes. <para><paramref name="mode"/> 不是三个已记载 crash 模式之一。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirm"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the Bootloader stage or the
    /// transport is disconnected. <para>会话不处于 Bootloader 阶段或传输已断开。</para></exception>
    /// <exception cref="MtkProtocolException">A mandatory echo or the first status word was rejected
    /// or malformed; the session faults. <para>必需的 echo 或第一个状态字被拒绝或格式错误；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before the exchange was
    /// submitted. <para>交互提交之前调用方取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkDaCrashResult> CrashDaAsync(MtkDaCrashMode mode, bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "Only the three documented download-agent crash modes are accepted.");
        }

        if (!confirm)
        {
            throw new UnauthorizedAccessException(
                "Crashing the download agent interrupts the boot channel and requires an explicit confirmation.");
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Bootloader);
            cancellationToken.ThrowIfCancellationRequested();
            MtkDaCrashResult result;
            try
            {
                result = mode switch
                {
                    MtkDaCrashMode.Dasend1 => await CrashDasend1OwnedAsync(cancellationToken).ConfigureAwait(false),
                    MtkDaCrashMode.Daread => await CrashDareadOwnedAsync(cancellationToken).ConfigureAwait(false),
                    _ => await CrashDasend2OwnedAsync(cancellationToken).ConfigureAwait(false)
                };
            }
            catch
            {
                _state = MtkSessionState.Faulted;
                throw;
            }

            RecordSecurityEvidence("da-crash", CommandOutcomeKind.Completed, result.Detail);
            _state = MtkSessionState.Faulted;
            return result;
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Runs the mismatched DA-send crash: reference mode 0.
    /// <para>执行大小不匹配的 DA 发送 crash：参考模式 0。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The crash result. crash 结果。</returns>
    private async Task<MtkDaCrashResult> CrashDasend1OwnedAsync(CancellationToken cancellationToken)
    {
        await EchoAsync([DaCrashSendCommand], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(0, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(DaCrashDasend1Bytes, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(DaCrashDasend1Bytes, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync("DA crash send request", cancellationToken).ConfigureAwait(false);
        var payload = new byte[(int)DaCrashDasend1Bytes];
        await WriteAsync(payload, _timeoutMs, cancellationToken).ConfigureAwait(false);
        var trailing = await TryReadUploadTrailingAsync(cancellationToken).ConfigureAwait(false);
        return new MtkDaCrashResult(MtkDaCrashMode.Dasend1, trailing.Completed, trailing.Status,
            trailing.Completed
                ? $"The target answered the size/signature-mismatched DA send of 0x{DaCrashDasend1Bytes:X} bytes " +
                  $"(status 0x{trailing.Status:X4}); it may have survived the crash attempt."
                : "The target stopped answering after the size/signature-mismatched DA send; the crash attempt " +
                  "was submitted.");
    }

    /// <summary>Runs the wide DA-read crash: reference mode 1.
    /// <para>执行宽范围 DA 读取 crash：参考模式 1。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The crash result. crash 结果。</returns>
    private async Task<MtkDaCrashResult> CrashDareadOwnedAsync(CancellationToken cancellationToken)
    {
        await EchoAsync([DaCrashReadCommand], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(0, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(DaCrashDareadWords, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync("DA crash read request", cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await ReadAsync(checked((int)DaCrashDareadWords * 4), _timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            var status = BinaryPrimitives.ReadUInt16BigEndian(
                await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
            return new MtkDaCrashResult(MtkDaCrashMode.Daread, true, status,
                $"The target streamed {DaCrashDareadWords * 4} bytes and closed the wide read with status " +
                $"0x{status:X4}; it may have survived the crash attempt.");
        }
        catch (Exception error) when (IsCrashSilence(error))
        {
            return new MtkDaCrashResult(MtkDaCrashMode.Daread, false, null,
                "The target stopped answering during the wide DA read; the crash attempt was submitted.");
        }
    }

    /// <summary>Runs the payload-and-jump DA crash: reference mode 2.
    /// <para>执行载荷加跳转的 DA crash：参考模式 2。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The crash result. crash 结果。</returns>
    private async Task<MtkDaCrashResult> CrashDasend2OwnedAsync(CancellationToken cancellationToken)
    {
        var payload = CreateDasend2CrashPayload();
        await EchoAsync([DaCrashSendCommand], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(0, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(DaCrashDasend2Bytes, cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(0, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync("DA crash payload request", cancellationToken).ConfigureAwait(false);
        await WriteAsync(payload, _timeoutMs, cancellationToken).ConfigureAwait(false);
        var trailing = await TryReadUploadTrailingAsync(cancellationToken).ConfigureAwait(false);
        if (!trailing.Completed)
        {
            return new MtkDaCrashResult(MtkDaCrashMode.Dasend2, false, trailing.Status,
                "The target stopped answering after the crash-payload upload, before the jump was submitted.");
        }

        await EchoAsync([DaCrashJumpCommand], cancellationToken).ConfigureAwait(false);
        await EchoUInt32Async(0, cancellationToken).ConfigureAwait(false);
        try
        {
            var jump = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
            var status = BinaryPrimitives.ReadUInt16BigEndian(
                await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
            var echoedAddress = BinaryPrimitives.ReadUInt32BigEndian(jump);
            return new MtkDaCrashResult(MtkDaCrashMode.Dasend2, true, status,
                echoedAddress != 0 || status != 0
                    ? $"The target answered the crash jump with address 0x{echoedAddress:X8} and status " +
                      $"0x{status:X4}; it may have survived the crash attempt."
                    : "The target acknowledged the crash jump to address 0; reconnect and re-handshake to " +
                      "confirm it fell back to Boot ROM.");
        }
        catch (Exception error) when (IsCrashSilence(error))
        {
            return new MtkDaCrashResult(MtkDaCrashMode.Dasend2, false, trailing.Status,
                "The target executed the crash jump and stopped answering; the crash attempt succeeded as far " +
                    "as it can be observed.");
        }
    }

    /// <summary>Reads the completion word pair an upload normally returns and reports whether the
    /// target answered at all. <para>读取上传通常返回的完成字对，并报告目标是否有应答。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Whether the trailing exchange completed and its status word when observed.
    /// <para>尾部交换是否完成，以及观测到时的状态字。</para></returns>
    private async Task<(bool Completed, ushort? Status)> TryReadUploadTrailingAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var completion = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
            return (true, BinaryPrimitives.ReadUInt16BigEndian(completion.AsSpan(2)));
        }
        catch (Exception error) when (IsCrashSilence(error))
        {
            return (false, null);
        }
    }

    /// <summary>Reports whether an exception means the crashed agent stopped talking rather than a
    /// caller cancellation. <para>报告某异常是否意味着被打崩的代理停止说话，而不是调用方取消。</para></summary>
    /// <param name="error">The exception raised by a trailing read.
    /// <para>尾部读取抛出的异常。</para></param>
    /// <returns><see langword="true"/> for silence, <see langword="false"/> for cancellation.
    /// <para>静默时为 <see langword="true"/>，取消时为 <see langword="false"/>。</para></returns>
    private static bool IsCrashSilence(Exception error) =>
        error is TimeoutException or MtkProtocolException;

    /// <summary>Builds the documented crash payload: one load followed by an undefined instruction,
    /// padded with zeroes to 0x118 bytes (Library/exploit_handler.py:154).
    /// <para>构造已记载的 crash 载荷：一条加载后接一条未定义指令，补零到 0x118 字节
    /// （Library/exploit_handler.py:154）。</para></summary>
    /// <returns>The exact payload bytes to upload. 要上传的精确载荷字节。</returns>
    private static byte[] CreateDasend2CrashPayload()
    {
        var payload = new byte[(int)DaCrashDasend2Bytes];
        ReadOnlySpan<byte> crashSequence = [0x00, 0x01, 0x9F, 0xE5, 0x10, 0xFF, 0x2F, 0xE1];
        crashSequence.CopyTo(payload);
        return payload;
    }
}