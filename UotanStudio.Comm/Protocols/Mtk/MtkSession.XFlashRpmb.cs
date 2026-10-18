using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const uint XFlashDeviceControl = 0x010009;
    private const uint XFlashGetRpmbStatus = 0x040010;
    private const uint XFlashCustomReadRpmb = 0x0F0006;
    private const uint XFlashCustomReadUfsRpmb = 0x0F0009;

    /// <summary>Queries the agent's opaque RPMB status reply and reports the RPMB capacity from the discovered geometry.
    /// <para>查询代理不透明的 RPMB 状态应答，并报告已发现几何信息中的 RPMB 容量。</para></summary>
    /// <remarks>The reference client sends the device-control code, checks that the exchange completed and returns the payload undecoded (<c>mtkclient/Library/DA/xflash/xflash_lib.py:590-598</c>), so the reply bytes are recorded as evidence instead of interpreted. The device-control envelope itself is <c>mtkclient/Library/DA/xflash/xflash_lib.py:176-191</c> with <c>DEVICE_CTRL = 0x010009</c> and <c>GET_RPMB_STATUS = 0x040010</c> from <c>mtkclient/Library/DA/xflash/xflash_param.py:14</c> and <c>mtkclient/Library/DA/xflash/xflash_param.py:56</c>.
    /// <para>参考客户端发送设备控制码、只确认交换完成后即原样返回载荷 （<c>mtkclient/Library/DA/xflash/xflash_lib.py:590-598</c>），因此应答字节只作为证据记录而不作解释。 设备控制封装本身见 <c>mtkclient/Library/DA/xflash/xflash_lib.py:176-191</c>，控制码取自 <c>mtkclient/Library/DA/xflash/xflash_param.py:14</c>（<c>DEVICE_CTRL = 0x010009</c>）与 <c>mtkclient/Library/DA/xflash/xflash_param.py:56</c>（<c>GET_RPMB_STATUS = 0x040010</c>）。</para></remarks>
    /// <param name="options">Optional bounds and deadline; <see langword="null"/> uses the read-only defaults.
    /// <para>可选的边界与截止时间；<see langword="null"/> 使用只读默认值。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The undecoded reply, its length and the reported RPMB capacity.
    /// <para>未解码的应答、其长度以及上报的 RPMB 容量。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not storage-ready, the XFlash channel is gone, or the active storage family has no RPMB area.
    /// <para>会话未处于存储就绪状态、XFlash 通道已失效，或当前存储族没有 RPMB 区域。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> is out of bounds.
    /// <para><paramref name="options"/> 越界。</para></exception>
    /// <exception cref="MtkProtocolException">A control frame was malformed or rejected; the family faults.
    /// <para>控制帧格式错误或被拒绝；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired; the family faults.
    /// <para>交换截止时间到期；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkRpmbStatusResult> QueryXFlashRpmbStatusAsync(MtkRpmbOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = ResolveRpmbOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            var target = RequireXFlashRpmbTarget();
            var capacity = ResolveXFlashRpmbCapacity(target);
            byte[] reply = [];
            await RunRpmbExchangeAsync(async token =>
            {
                var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
                reply = await QueryXFlashControlAsync(channel, XFlashGetRpmbStatus, token).ConfigureAwait(false);
            }, effective.OperationTimeoutMs, FaultXFlashStorage, cancellationToken).ConfigureAwait(false);
            RecordRpmbEvidence("rpmb-status", CommandOutcomeKind.Completed,
                $"xflash GET_RPMB_STATUS 0x{XFlashGetRpmbStatus:X8} for {target}; reply kept undecoded.",
                Convert.ToHexString(reply));
            return new MtkRpmbStatusResult
            {
                RawReply = reply,
                ReportedRpmbCapacityBytes = capacity
            };
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads a bounded run of RPMB frames through the XFlash DA-extension read command.
    /// <para>通过 XFlash DA 扩展读取命令读取有界的连续 RPMB 帧。</para></summary>
    /// <remarks>Each frame is one complete command exchange: the device-control envelope, the read control code, the frame index as a four-byte little-endian field, a two-byte result frame and then the frame data (<c>mtkclient/Library/DA/xflash/extension/xflash.py:395-411</c>, codes at <c>mtkclient/Library/DA/xflash/extension/xflash.py:21-24</c>). The first frame the device refuses stops the run; the session never retries a refused frame.
    /// <para>每一帧都是一次完整命令交换：设备控制封装、读取控制码、以小端四字节发送的帧序号、 一个两字节结果帧，随后才是帧数据（<c>mtkclient/Library/DA/xflash/extension/xflash.py:395-411</c>， 控制码见 <c>mtkclient/Library/DA/xflash/extension/xflash.py:21-24</c>）。设备拒绝的首帧会终止本次读取； 会话绝不会重试被拒绝的帧。</para></remarks>
    /// <param name="range">The validated frame-aligned range to read.
    /// <para>要读取的、已校验的帧对齐范围。</para></param>
    /// <param name="options">Optional bounds and deadline; <see langword="null"/> uses the read-only defaults.
    /// <para>可选的边界与截止时间；<see langword="null"/> 使用只读默认值。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>One result per addressed frame plus the concatenated delivered bytes.
    /// <para>每个已寻址帧的结果，以及拼接后的已交付字节。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty, unbounded, exceeds the reported RPMB capacity or exceeds the options ceiling.
    /// <para>范围为空、无界、超出上报的 RPMB 容量或超出选项上限。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not storage-ready, the device reported no RPMB capacity, or the channel is gone.
    /// <para>会话未处于存储就绪状态、设备未上报 RPMB 容量，或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">A result or data frame is malformed or the wrong width; the family faults without retry.
    /// <para>结果帧或数据帧格式错误、宽度不符；该协议族进入 fault 状态且不重试。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired; the family faults.
    /// <para>交换截止时间到期；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="NotSupportedException">The active storage family is NOR or NAND.
    /// <para>当前存储族为 NOR 或 NAND。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkRpmbReadResult> ReadXFlashRpmbFramesAsync(MtkRpmbFrameRange range,
        MtkRpmbOptions? options = null, CancellationToken cancellationToken = default)
    {
        var effective = ResolveRpmbOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            var target = RequireXFlashRpmbTarget();
            var capacity = ResolveXFlashRpmbCapacity(target);
            range.Validate(capacity, effective.MaximumReadFrames);
            return await ReadXFlashRpmbFramesOwnedAsync(range, target, capacity, effective, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Dumps the whole reported RPMB area of the active block-storage family.
    /// <para>转储当前块存储族完整上报的 RPMB 区域。</para></summary>
    /// <remarks>The reference client derives the frame count by dividing the reported eMMC RPMB size by the fixed frame width (<c>mtkclient/Library/DA/xflash/extension/xflash.py:478-498</c>); the UFS branch hardcodes a count instead of reading a size, and the UFS storage reply has no RPMB field at all (<c>mtkclient/Library/DA/xflash/xflash_lib.py:620-636</c>), so a UFS dump needs an explicit frame range.
    /// <para>参考客户端用上报的 eMMC RPMB 大小除以固定帧宽度推导出帧数量 （<c>mtkclient/Library/DA/xflash/extension/xflash.py:478-498</c>）；UFS 分支硬编码数量而不读取大小， 且 UFS 存储应答根本没有 RPMB 字段（<c>mtkclient/Library/DA/xflash/xflash_lib.py:620-636</c>）， 因此 UFS 转储必须显式给出帧范围。</para></remarks>
    /// <param name="options">Optional bounds and deadline; <see langword="null"/> uses the read-only defaults.
    /// <para>可选的边界与截止时间；<see langword="null"/> 使用只读默认值。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The delivered frames as one opaque blob plus the reported capacity.
    /// <para>以不透明数据块交付的帧，以及上报的容量。</para></returns>
    /// <exception cref="NotSupportedException">The active family reports no RPMB size field, which is the UFS case for XFlash.
    /// <para>当前族未上报 RPMB 大小字段，即 XFlash 的 UFS 情形。</para></exception>
    /// <exception cref="InvalidOperationException">The reported RPMB capacity is zero, absent, or not an exact multiple of the frame width.
    /// <para>上报的 RPMB 容量为零、缺失，或不是帧宽度的整数倍。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The area needs more frames than <see cref="MtkRpmbOptions.MaximumReadFrames"/>.
    /// <para>该区域所需帧数超过 <see cref="MtkRpmbOptions.MaximumReadFrames"/>。</para></exception>
    /// <exception cref="MtkProtocolException">A frame reply violated the protocol; the family faults.
    /// <para>某帧应答违反协议；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired; the family faults.
    /// <para>交换截止时间到期；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkRpmbDeviceBlob> ReadXFlashRpmbDeviceBlobAsync(MtkRpmbOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = ResolveRpmbOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            var target = RequireXFlashRpmbTarget();
            var capacity = ResolveXFlashRpmbCapacity(target)
                ?? throw new NotSupportedException("The XFlash UFS geometry reports no RPMB capacity; read an explicit frame range instead.");
            if (capacity % MtkRpmbWire.FrameLength != 0 || capacity / MtkRpmbWire.FrameLength > int.MaxValue)
            {
                throw new InvalidOperationException("The reported RPMB capacity is not an exact frame multiple.");
            }
            var range = new MtkRpmbFrameRange(0, (int)(capacity / MtkRpmbWire.FrameLength));
            range.Validate(capacity, effective.MaximumReadFrames);
            var read = await ReadXFlashRpmbFramesOwnedAsync(range, target, capacity, effective, cancellationToken).ConfigureAwait(false);
            return new MtkRpmbDeviceBlob
            {
                Target = target,
                RawBytes = read.RawBytes,
                FrameCount = read.Frames.Count,
                ReportedRpmbCapacityBytes = capacity
            };
        }
        finally
        {
            _operation.Release();
        }
    }

    private MtkRpmbTarget RequireXFlashRpmbTarget()
    {
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        return storage.Kind switch
        {
            MtkXFlashStorageKind.Emmc => MtkRpmbTarget.Emmc,
            MtkXFlashStorageKind.Ufs => MtkRpmbTarget.Ufs,
            _ => throw new NotSupportedException("The selected XFlash storage family exposes no RPMB area.")
        };
    }

    private long? ResolveXFlashRpmbCapacity(MtkRpmbTarget target)
    {
        if (target == MtkRpmbTarget.Ufs)
        {
            return null;
        }
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        var region = storage.Regions.FirstOrDefault(region => region.RequiresAuthenticatedAccess)
            ?? throw new InvalidOperationException("The reported XFlash geometry has no RPMB region.");
        return region.CapacityBytes;
    }

    private async Task<MtkRpmbReadResult> ReadXFlashRpmbFramesOwnedAsync(MtkRpmbFrameRange range,
        MtkRpmbTarget target, long? capacity, MtkRpmbOptions options, CancellationToken cancellationToken)
    {
        var frames = new List<MtkRpmbFrameResult>();
        await RunRpmbExchangeAsync(async token =>
        {
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            for (var index = 0; index < range.FrameCount; index++)
            {
                var frame = checked(range.StartFrame + index);
                var (outcome, data) = await ReadXFlashRpmbFrameAsync(channel, target, frame, token).ConfigureAwait(false);
                frames.Add(new MtkRpmbFrameResult
                {
                    Frame = frame,
                    Outcome = outcome,
                    RawFrame = outcome.IsOkay ? data : []
                });
                if (!outcome.IsOkay)
                {
                    break;
                }
            }
        }, options.OperationTimeoutMs, FaultXFlashStorage, cancellationToken).ConfigureAwait(false);
        var accepted = frames.Count(frame => frame.Outcome.IsOkay);
        RecordRpmbEvidence("rpmb-read", accepted == range.FrameCount ? CommandOutcomeKind.Completed : CommandOutcomeKind.Rejected,
            $"xflash rpmb read {(target == MtkRpmbTarget.Emmc ? XFlashCustomReadRpmb : XFlashCustomReadUfsRpmb):X8} " +
            $"frames {range.StartFrame}+{range.FrameCount}; {accepted} accepted.");
        return new MtkRpmbReadResult
        {
            Target = target,
            Requested = range,
            Frames = frames,
            RawBytes = JoinRpmbFrameBytes(frames),
            ReportedRpmbCapacityBytes = capacity
        };
    }

    private async Task<(MtkRpmbFrameOutcome Outcome, byte[] Data)> ReadXFlashRpmbFrameAsync(MtkXFlashChannel channel,
        MtkRpmbTarget target, long frame, CancellationToken cancellationToken)
    {
        var command = target == MtkRpmbTarget.Emmc ? XFlashCustomReadRpmb : XFlashCustomReadUfsRpmb;
        await RequireXFlashRpmbCommandAsync(channel, command, cancellationToken).ConfigureAwait(false);
        await channel.SendAsync(1, CreateRpmbFrameWord(frame), cancellationToken).ConfigureAwait(false);
        var reply = await channel.ReceiveAsync(2, cancellationToken).ConfigureAwait(false);
        if (reply.DataType != 1)
        {
            throw new MtkProtocolException("The RPMB frame result arrived in a non-protocol frame.");
        }
        var outcome = ReadRpmbFrameOutcome(reply.Payload);
        if (!outcome.IsOkay)
        {
            var reported = await ReadXFlashValueAsync(channel, "rpmb frame refusal", cancellationToken).ConfigureAwait(false);
            RecordRpmbEvidence("rpmb-frame", CommandOutcomeKind.Rejected,
                $"xflash rpmb frame {frame} reported 0x{outcome.ReportedCode:X4}; completion status 0x{reported:X8} kept as evidence.");
            return (outcome, []);
        }
        var data = await channel.ReceiveAsync(MtkRpmbWire.FrameLength, cancellationToken).ConfigureAwait(false);
        if (data.DataType != 1)
        {
            throw new MtkProtocolException("RPMB frame data arrived in a non-protocol frame.");
        }
        if (data.Payload.Length != MtkRpmbWire.FrameLength)
        {
            throw new MtkProtocolException("The RPMB frame data does not match the fixed frame width.");
        }
        await ReadXFlashStatusAsync(channel, "rpmb frame data", cancellationToken).ConfigureAwait(false);
        return (outcome, data.Payload);
    }

    private async Task RequireXFlashRpmbCommandAsync(MtkXFlashChannel channel, uint command, CancellationToken cancellationToken)
    {
        await SendXFlashWordAsync(channel, XFlashDeviceControl, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "rpmb device control", cancellationToken).ConfigureAwait(false);
        await SendXFlashWordAsync(channel, command, cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "rpmb command", cancellationToken).ConfigureAwait(false);
    }
}