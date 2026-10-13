using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const uint XFlashSecurityDeviceControl = 0x010009;
    private const uint XFlashChipIdControlCode = 0x04000D;
    private const uint XFlashSecurityCustomReadRegister = 0x0F0002;
    private const uint XFlashSecurityCustomWriteRegister = 0x0F0004;
    private const uint XFlashSecurityCustomSetKey = 0x0F000B;
    private const byte LegacySecurityReadRegisterCommand = 0x7A;
    private const byte LegacySecurityWriteRegisterCommand = 0x7B;
    private const byte LegacySecurityAcknowledge = 0x5A;
    private const string SecCfgPartitionName = "seccfg";
    private const int MaximumSecCfgBytes = 4 * 1024 * 1024;

    /// <summary>Accepts the stages at which a device-control query is available to the caller.
    /// <para>接受调用方可用器件控制查询的阶段。</para></summary>
    /// <remarks>
    /// The reference client issues get-style device-control queries right after DA1 setup, so the
    /// stage-one family plus every later XFlash stage are admissible; a storage-only stage does not
    /// imply the agent is still responsive after a fault.
    /// 参考客户端在 DA1 设置后立即发起获取类器件控制查询，因此阶段一并及所有后续 XFlash 阶段均可接受；
    /// 进入 fault 后，仅有存储就绪阶段并不意味着代理仍可响应。
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transport is disconnected or the session is in
    /// another stage. <para>传输已断开或会话处于其他阶段。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    private void RequireXFlashSecurityControlStage()
    {
        ThrowIfDisposed();
        if (!Transport.IsConnected || _state is not (MtkSessionState.XFlashStageOneReady
            or MtkSessionState.XFlashStageTwoStarted or MtkSessionState.XFlashStorageReady))
        {
            throw new InvalidOperationException(
                $"The XFlash device-control query needs an initialized DA1 stage; current state is {_state}.");
        }
    }

    /// <summary>Accepts only the stages where the patched stage-two agent answers custom control codes.
    /// <para>仅接受已打补丁的二阶段代理会应答自定义控制码的阶段。</para></summary>
    /// <remarks>
    /// Every custom code reproduced here is dispatched from the stage-two extension of the reference
    /// client (Library/DA/xflash/extension/xflash.py:302-352 and :395-464), which is only reachable
    /// after DA2 has started, so stage one is refused rather than attempted.
    /// 此处复现的每个自定义码都由参考客户端的二阶段扩展发出
    /// （Library/DA/xflash/extension/xflash.py:302-352 与 :395-464），而该扩展仅在 DA2 启动后可达，
    /// 因此阶段一会被拒绝而不是被尝试。
    /// </remarks>
    /// <returns>The live XFlash channel. 当前存活的 XFlash 通道。</returns>
    /// <exception cref="InvalidOperationException">DA2 has not started, the transport is disconnected
    /// or the channel is gone. <para>DA2 未启动、传输已断开或通道已失效。</para></exception>
    private MtkXFlashChannel RequireXFlashSecurityCustomStage()
    {
        ThrowIfDisposed();
        if (!Transport.IsConnected || _state is not (MtkSessionState.XFlashStageTwoStarted
            or MtkSessionState.XFlashStorageReady))
        {
            throw new InvalidOperationException(
                $"The XFlash custom control code needs a started DA2 stage; current state is {_state}.");
        }

        return _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
    }

    /// <summary>Sends the device-control selector and one control code, checking both status replies.
    /// <para>发送器件控制选择器与一个控制码，并检查两次状态回复。</para></summary>
    /// <remarks>
    /// This mirrors the reference dispatcher, which writes the device-control command, waits for a
    /// status, writes the control code and waits for a second status before any payload exchange
    /// (Library/DA/xflash/extension/xflash.py:302-313 with Library/DA/xflash/xflash_param.py:14).
    /// 这里与参考分发器一致：先写器件控制命令并等待状态，再写控制码并等待第二个状态，之后才交换载荷
    /// （Library/DA/xflash/extension/xflash.py:302-313，命令取值见
    /// Library/DA/xflash/xflash_param.py:14）。
    /// </remarks>
    /// <param name="channel">The live XFlash channel. 当前存活的 XFlash 通道。</param>
    /// <param name="controlCode">The control code to select. 要选择的控制码。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private async Task SendXFlashSecurityControlCodeAsync(MtkXFlashChannel channel, uint controlCode,
        CancellationToken cancellationToken)
    {
        await SendXFlashWordAsync(channel, XFlashSecurityDeviceControl, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "device control", cancellationToken).ConfigureAwait(false);
        await SendXFlashWordAsync(channel, controlCode, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, $"device-control code 0x{controlCode:X8}", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads one protocol payload frame and rejects any other frame type.
    /// <para>读取一个协议载荷帧，并拒绝其他帧类型。</para></summary>
    /// <param name="channel">The live XFlash channel. 当前存活的 XFlash 通道。</param>
    /// <param name="maximumPayload">The ceiling checked before the frame is buffered.
    /// <para>在缓存帧之前校验的上限。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The payload bytes exactly as reported. 按上报原样返回的载荷字节。</returns>
    /// <exception cref="MtkProtocolException">The frame is not a protocol frame.
    /// <para>该帧不是协议帧。</para></exception>
    private async Task<byte[]> ReadXFlashSecurityPayloadAsync(MtkXFlashChannel channel, int maximumPayload,
        CancellationToken cancellationToken)
    {
        var frame = await channel.ReceiveAsync(maximumPayload, cancellationToken).ConfigureAwait(false);
        if (frame.DataType != 1)
        {
            throw new MtkProtocolException("A security command returned a non-protocol frame.");
        }

        return frame.Payload;
    }

    /// <summary>Reads one 32-bit register through the stage-two custom register code.
    /// <para>通过二阶段自定义寄存器码读取一个 32 位寄存器。</para></summary>
    /// <remarks>
    /// The framing is the documented register read: device-control selector, control code, one
    /// little-endian address frame, one payload frame and a completion status
    /// (Library/DA/xflash/extension/xflash.py:325-332 with :302-313).
    /// 帧结构即已记载的寄存器读取：器件控制选择器、控制码、一个小端地址帧、一个载荷帧与完成状态
    /// （Library/DA/xflash/extension/xflash.py:325-332，配合 :302-313）。
    /// </remarks>
    /// <param name="channel">The live XFlash channel. 当前存活的 XFlash 通道。</param>
    /// <param name="address">The register address. 寄存器地址。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The four reply bytes. 四个回复字节。</returns>
    /// <exception cref="MtkProtocolException">The reply is not the documented doubleword width.
    /// <para>回复不是已记载的双字宽度。</para></exception>
    private async Task<byte[]> ReadXFlashSecurityRegisterAsync(MtkXFlashChannel channel, uint address,
        CancellationToken cancellationToken)
    {
        await SendXFlashSecurityControlCodeAsync(channel, XFlashSecurityCustomReadRegister, cancellationToken).ConfigureAwait(false);
        await SendXFlashWordAsync(channel, address, cancellationToken).ConfigureAwait(false);
        var payload = await ReadXFlashSecurityPayloadAsync(channel, MtkEfuseAccess.CellLength, cancellationToken)
            .ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "register read", cancellationToken).ConfigureAwait(false);
        if (payload.Length != MtkEfuseAccess.CellLength)
        {
            throw new MtkProtocolException(
                $"The register read reply must be {MtkEfuseAccess.CellLength} bytes; the agent sent {payload.Length}.");
        }

        return payload;
    }

    /// <summary>Reads a 32-bit device register through the Legacy register command.
    /// <para>通过 Legacy 寄存器命令读取 32 位设备寄存器。</para></summary>
    /// <remarks>
    /// The documented exchange writes the register-read command byte, a big-endian address, then
    /// reads four big-endian value bytes followed by a single acknowledge byte
    /// (Library/DA/legacy/dalegacy_lib.py:92-99 with Library/DA/legacy/dalegacy_param.py:31-32).
    /// 已记载的交互写入寄存器读命令字节与大端地址，然后读取 4 个大端值字节与 1 个确认字节
    /// （Library/DA/legacy/dalegacy_lib.py:92-99，命令取值见
    /// Library/DA/legacy/dalegacy_param.py:31-32）。
    /// </remarks>
    /// <param name="address">The register address. 寄存器地址。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The register value. 寄存器取值。</returns>
    /// <exception cref="MtkProtocolException">The acknowledgement byte is missing or negative.
    /// <para>确认字节缺失或为否定确认。</para></exception>
    private async Task<uint> ReadLegacySecurityRegisterAsync(uint address, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var request = new byte[5];
        request[0] = LegacySecurityReadRegisterCommand;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(1), address);
        await WriteAsync(request, _timeoutMs, cancellationToken).ConfigureAwait(false);
        var value = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
        await RequireLegacySecurityAcknowledgeAsync("register read", cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(value);
    }

    private async Task RequireLegacySecurityAcknowledgeAsync(string operation, CancellationToken cancellationToken)
    {
        var acknowledgement = (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0];
        if (acknowledgement != LegacySecurityAcknowledge)
        {
            throw new MtkProtocolException(
                $"Legacy {operation} was not acknowledged (0x{acknowledgement:X2}).", acknowledgement);
        }
    }

    /// <summary>Locates the secure-boot configuration partition in a validated GPT snapshot.
    /// <para>在已校验的 GPT 快照中定位安全启动配置分区。</para></summary>
    /// <remarks>
    /// The reference client selects the entry whose name is exactly <c>seccfg</c> and aborts when no
    /// such entry exists (Library/DA/xflash/extension/xflash.py:572-581 and
    /// Library/DA/legacy/extension/legacy.py:172-181); the shared resolver adds its own
    /// ambiguity check, so a duplicated name is refused instead of reading the first match.
    /// 参考客户端精确选择名为 <c>seccfg</c> 的条目，不存在时中止
    /// （Library/DA/xflash/extension/xflash.py:572-581 与
    /// Library/DA/legacy/extension/legacy.py:172-181）；共用的解析器另有歧义检查，因此重名会被拒绝，
    /// 而不是读取首个匹配项。
    /// </remarks>
    /// <param name="table">The validated partition table. 已校验的分区表。</param>
    /// <returns>The single entry carrying the documented name. 唯一携带该已记载名称的条目。</returns>
    /// <exception cref="KeyNotFoundException">No entry is named <c>seccfg</c>.
    /// <para>没有条目名为 <c>seccfg</c>。</para></exception>
    /// <exception cref="InvalidDataException">More than one entry carries the name.
    /// <para>多条条目携带该名称。</para></exception>
    /// <exception cref="NotSupportedException">The entry range is larger than
    /// <see cref="MaximumSecCfgBytes"/>. <para>条目范围超过 <see cref="MaximumSecCfgBytes"/>。</para></exception>
    private static GptPartition ResolveSecCfgPartition(GptTable table)
    {
        var entry = table.FindPartition(SecCfgPartitionName);
        if (entry.ByteOffset < 0 || entry.ByteLength <= 0 || entry.ByteLength > MaximumSecCfgBytes)
        {
            throw new NotSupportedException(
                $"The seccfg range 0x{entry.ByteOffset:X}+0x{entry.ByteLength:X} is outside the supported bound.");
        }

        return entry;
    }

    private void RecordSecurityEvidence(string phase, CommandOutcomeKind outcome, string detail, string? rawHex = null)
        => _evidence.Add(new ProtocolEvidence
        {
            Protocol = "mtk",
            Phase = phase,
            Outcome = outcome,
            Detail = detail,
            RawResponseHex = rawHex
        });

    /// <summary>Reports whether an exception is a host-side refusal rather than a broken exchange.
    /// <para>报告某个异常是主机侧拒绝，而非被中断的交互。</para></summary>
    /// <remarks>
    /// A missing write opt-in, an undocumented layout, an already-applied state or a mismatching
    /// supplied image is decided before any byte leaves the host, so the agent is still in the state
    /// the caller expects and the session must stay usable. A protocol, transport or cancellation
    /// failure is not a refusal and still faults the family.
    /// <para>缺少写入选择、未记载的布局、已处于目标状态或提供的镜像不匹配，都在任何字节离开主机之前决定，
    /// 因此代理仍处于调用方期望的状态，会话必须保持可用。协议、传输或取消失败不属于拒绝，仍会使家族进入
    /// fault 状态。</para>
    /// </remarks>
    /// <param name="exception">The exception raised inside a security command.
    /// <para>在安全命令内部抛出的异常。</para></param>
    /// <returns><see langword="true"/> when the session must keep its state.
    /// <para>会话需要保持状态时为 <see langword="true"/>。</para></returns>
    private static bool IsSecurityPolicyRefusal(Exception exception) => exception is UnauthorizedAccessException
        or NotSupportedException or InvalidOperationException or ArgumentException;

    /// <summary>Faults the XFlash family after an interrupted security exchange.
    /// <para>在被中断的安全交互之后使 XFlash 家族进入 fault 状态。</para></summary>
    /// <remarks>
    /// Only the XFlash channel is dropped and only the XFlash security state is cleared; the
    /// storage inventory and the Legacy and XML stages belong to other families and stay as they
    /// were, matching how the existing XFlash command paths fault.
    /// 只丢弃 XFlash 通道并只清除 XFlash 安全状态；存储清单以及 Legacy 与 XML 阶段属于其他家族，
    /// 保持原状，与现有 XFlash 命令路径的 fault 方式一致。
    /// </remarks>
    private void FaultXFlashSecurity()
    {
        _xflash = null;
        _state = MtkSessionState.Faulted;
    }
}