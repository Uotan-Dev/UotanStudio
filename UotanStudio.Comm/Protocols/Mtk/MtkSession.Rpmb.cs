using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Reads the two-byte little-endian RPMB frame result and keeps the reported number.
    /// <para>读取小端两字节的 RPMB 帧结果并保留上报数值。</para>
    /// </summary>
    /// <param name="reply">The reply payload the agent delivered for the addressed frame.
    /// <para>代理针对已寻址帧交付的应答载荷。</para></param>
    /// <returns>
    /// The raw code plus its named meaning when the reference table supplies one.
    /// <para>原始数值，以及参考对照表提供含义时的已命名结果。</para>
    /// </returns>
    /// <exception cref="MtkProtocolException">
    /// The reply is empty or wider than eight bytes; the exchange faults and is never replayed.
    /// <para>应答为空或超过八字节；交换进入 fault 状态且绝不重放。</para>
    /// </exception>
    /// <remarks>
    /// The reference client treats any frame whose length is not <c>0x100</c> as the little-endian error code
    /// (<c>mtkclient/Library/DA/xflash/extension/xflash.py:478-497</c> and
    /// <c>mtkclient/Library/DA/xml/extension/v6.py:626-649</c>). The named error codes are small, so the low
    /// sixteen bits are reported while wider codes stay visibly uninterpreted.
    /// <para>参考客户端把任何长度不是 <c>0x100</c> 的帧视为小端错误码
    /// （<c>mtkclient/Library/DA/xflash/extension/xflash.py:478-497</c> 与
    /// <c>mtkclient/Library/DA/xml/extension/v6.py:626-649</c>）。已命名的错误码都很小，因此上报低十六位，
    /// 更宽的错误码保持未解释状态。</para>
    /// </remarks>
    private static MtkRpmbFrameOutcome ReadRpmbFrameOutcome(ReadOnlySpan<byte> reply)
    {
        if (reply.Length is < 1 or > 8)
        {
            throw new MtkProtocolException("The RPMB frame result is empty or wider than eight bytes.");
        }
        var code = reply.Length == 1
            ? (ushort)reply[0]
            : BinaryPrimitives.ReadUInt16LittleEndian(reply);
        return new MtkRpmbFrameOutcome(code, code <= (ushort)MtkRpmbFrameStatus.AuthenticationKeyNotProgrammed
            ? (MtkRpmbFrameStatus)code
            : null);
    }

    /// <summary>
    /// Builds the single frame the reference client sends for an addressed RPMB frame index.
    /// <para>构造参考客户端针对已寻址 RPMB 帧序号发送的单个数据帧。</para>
    /// </summary>
    /// <param name="frame">The already validated zero-based frame index.
    /// <para>已经校验过的从零开始的帧序号。</para></param>
    /// <returns>A four-byte little-endian payload. <para>小端四字节载荷。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="frame"/> does not fit the 32-bit wire field.
    /// <para><paramref name="frame"/> 无法容纳于 32 位线协议字段。</para>
    /// </exception>
    /// <remarks>
    /// An integer argument is packed as <c>&lt;I</c> by the transport helper the RPMB commands reuse:
    /// <c>mtkclient/Library/DA/xflash/xflash_lib.py:93-101</c> and
    /// <c>mtkclient/Library/DA/xml/xml_lib.py:135-154</c>.
    /// <para>RPMB 命令复用的传输辅助函数会以 <c>&lt;I</c> 打包整数参数：
    /// <c>mtkclient/Library/DA/xflash/xflash_lib.py:93-101</c> 与
    /// <c>mtkclient/Library/DA/xml/xml_lib.py:135-154</c>。</para>
    /// </remarks>
    private static byte[] CreateRpmbFrameWord(long frame)
    {
        if (frame > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "The RPMB frame index exceeds the 32-bit wire field.");
        }
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)frame);
        return payload;
    }

    /// <summary>
    /// Rejects an RPMB write before the lease is taken unless the caller opted in.
    /// <para>除非调用方显式开启，否则在取得租约之前就拒绝 RPMB 写入。</para>
    /// </summary>
    /// <param name="options">The options supplied for the write. <para>本次写入提供的选项。</para></param>
    /// <exception cref="NotSupportedException">
    /// <see cref="MtkRpmbOptions.AllowAuthenticatedWrites"/> is <see langword="false"/>, which is the default.
    /// <para><see cref="MtkRpmbOptions.AllowAuthenticatedWrites"/> 为 <see langword="false"/>（默认值）。</para>
    /// </exception>
    private static void RequireRpmbWriteOptIn(MtkRpmbOptions options)
    {
        if (!options.AllowAuthenticatedWrites)
        {
            throw new NotSupportedException("RPMB frame writes require MtkRpmbOptions.AllowAuthenticatedWrites.");
        }
    }

    /// <summary>
    /// Substitutes the read-only default options when the caller supplies none.
    /// <para>调用方未提供选项时使用只读的默认选项。</para>
    /// </summary>
    /// <param name="options">The caller-supplied options, or <see langword="null"/>.
    /// <para>调用方提供的选项，或 <see langword="null"/>。</para></param>
    /// <returns>The validated options actually used. <para>实际使用且已校验的选项。</para></returns>
    private static MtkRpmbOptions ResolveRpmbOptions(MtkRpmbOptions? options)
    {
        var effective = options ?? new MtkRpmbOptions();
        effective.Validate();
        return effective;
    }

    /// <summary>
    /// Runs one RPMB exchange under its own cooperative deadline and faults only the active family.
    /// <para>在自身的协作式截止时间下运行一次 RPMB 交换，且仅使活动的协议族进入 fault。</para>
    /// </summary>
    /// <param name="exchange">The exchange body, which must honour the linked token.
    /// <para>交换主体，必须遵循链接的令牌。</para></param>
    /// <param name="timeoutMs">The whole-exchange budget already validated by the caller.
    /// <para>调用方已校验的整次交换时间预算。</para></param>
    /// <param name="fault">The family-specific fault action. <para>按协议族区分的 fault 动作。</para></param>
    /// <param name="cancellationToken">The caller's cancellation token.
    /// <para>调用方的取消令牌。</para></param>
    /// <exception cref="TimeoutException">
    /// The budget expired before the exchange completed; the family faults and must be reconnected.
    /// <para>预算在交换完成前到期；该协议族进入 fault 状态并必须重连。</para>
    /// </exception>
    private async Task RunRpmbExchangeAsync(Func<CancellationToken, Task> exchange, int timeoutMs, Action fault,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        try
        {
            await exchange(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            fault();
            throw new TimeoutException("The RPMB exchange deadline expired; reconnect before continuing.", error);
        }
        catch
        {
            fault();
            throw;
        }
    }

    /// <summary>
    /// Records what an RPMB command returned instead of trusting an interpretation of it.
    /// <para>记录 RPMB 命令返回了什么，而不是相信对其的某种解释。</para>
    /// </summary>
    /// <param name="phase">The evidence phase label. <para>证据阶段标签。</para></param>
    /// <param name="outcome">The observed outcome kind. <para>观测到的结果类型。</para></param>
    /// <param name="detail">A localization-neutral description of the observation.
    /// <para>与本地化无关的观测描述。</para></param>
    /// <param name="rawResponseHex">The reply bytes as hexadecimal text, when identity matters.
    /// <para>需要保留字节身份时的十六进制应答文本。</para></param>
    private void RecordRpmbEvidence(string phase, CommandOutcomeKind outcome, string detail, string? rawResponseHex = null)
    {
        _evidence.Add(new ProtocolEvidence
        {
            Protocol = "mtk",
            Phase = phase,
            Outcome = outcome,
            Detail = detail,
            RawResponseHex = rawResponseHex
        });
    }

    /// <summary>
    /// Validates a byte-addressed RPMB request against the reported capacity and the configured ceiling.
    /// <para>依据上报容量与配置上限校验按字节寻址的 RPMB 请求。</para>
    /// </summary>
    /// <param name="reportedCapacityBytes">The RPMB capacity the device reported.
    /// <para>设备上报的 RPMB 容量。</para></param>
    /// <param name="offset">The requested byte offset inside the RPMB area.
    /// <para>RPMB 区域内请求的字节偏移。</para></param>
    /// <param name="length">The requested byte count. <para>请求的字节数。</para></param>
    /// <param name="options">The bounds supplied by the caller. <para>调用方提供的边界。</para></param>
    /// <exception cref="InvalidOperationException">
    /// The device reported no RPMB capacity, so there is nothing to read.
    /// <para>设备未上报 RPMB 容量，因此没有可读取的内容。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The range is unaligned, empty, exceeds the configured ceiling or extends past the reported capacity.
    /// <para>范围未对齐、为空、超出配置上限或越过上报容量。</para>
    /// </exception>
    private static void ValidateRpmbByteRange(long reportedCapacityBytes, long offset, long length, MtkRpmbOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (reportedCapacityBytes == 0)
        {
            throw new InvalidOperationException("The device reported no RPMB capacity for the selected interface.");
        }
        if (offset % MtkRpmbWire.LegacyBlockSize != 0 || length % MtkRpmbWire.LegacyBlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The RPMB byte range is not block aligned.");
        }
        if (length > (long)options.MaximumReadFrames * MtkRpmbWire.FrameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The RPMB byte range exceeds the configured read ceiling.");
        }
        var end = checked(offset + length);
        if (end > reportedCapacityBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The requested RPMB byte range is outside the reported RPMB capacity.");
        }
    }

    /// <summary>
    /// Concatenates the delivered frame bytes in address order.
    /// <para>按地址顺序拼接已交付的帧字节。</para>
    /// </summary>
    /// <param name="frames">The per-frame results collected so far. <para>目前收集的逐帧结果。</para></param>
    /// <returns>A buffer sized to the accepted frames only. <para>仅按已接受帧长度分配的缓冲区。</para></returns>
    private static byte[] JoinRpmbFrameBytes(IReadOnlyList<MtkRpmbFrameResult> frames)
    {
        var accepted = 0;
        foreach (var frame in frames)
        {
            accepted += frame.RawFrame.Length;
        }
        var bytes = new byte[accepted];
        var position = 0;
        foreach (var frame in frames)
        {
            frame.RawFrame.CopyTo(bytes, position);
            position += frame.RawFrame.Length;
        }
        return bytes;
    }
}