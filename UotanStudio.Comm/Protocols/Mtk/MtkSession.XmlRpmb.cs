using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const string XmlReadRpmbCommand = "CMD:CUSTOMRPMBR";

    /// <summary>Reads a bounded run of RPMB frames through the XML DA's custom RPMB command.
    /// <para>通过 XML DA 的自定义 RPMB 命令读取有界的连续帧。</para></summary>
    /// <remarks>One exchange sends <c>CMD:CUSTOMRPMBR</c> once, waits for <c>OK</c>, sends the first sector and the sector count as two little-endian words, then reads one frame per sector: exactly 0x100 bytes is data and a short frame carries the error code. <c>CMD:END</c> and the following <c>CMD:START</c> close the command. The XML DA reports no RPMB status control code, so <see cref="QueryXFlashRpmbStatusAsync"/> has no counterpart here. The first frame the device refuses stops the frame loop and is never replayed.
    /// <para>一次交换只发送一次 <c>CMD:CUSTOMRPMBR</c> 并等待 <c>OK</c>，随后以小端四字节发送首帧与帧数量， 再为每个 sector 读取一帧：恰好 0x100 字节为数据，短帧携带错误码。最后以 <c>CMD:END</c> 与紧随的 <c>CMD:START</c> 收尾。XML DA 没有上报 RPMB 状态控制码， 因此 <see cref="QueryXFlashRpmbStatusAsync"/> 在此没有对应方法。设备拒绝的首帧会终止帧循环且绝不重放。</para></remarks>
    /// <param name="range">The validated frame-aligned range to read.
    /// <para>要读取的、已校验的帧对齐范围。</para></param>
    /// <param name="options">Optional bounds and deadline; <see langword="null"/> uses the read-only defaults.
    /// <para>可选的边界与截止时间；<see langword="null"/> 使用只读默认值。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>One result per addressed frame plus the concatenated delivered bytes.
    /// <para>每个已寻址帧的结果，以及拼接后的已交付字节。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty, unbounded or outside the reported RPMB capacity.
    /// <para>范围为空、无界或超出上报的 RPMB 容量。</para></exception>
    /// <exception cref="InvalidOperationException">The XML storage geometry is unknown, no channel is attached, or the reported RPMB area is empty.
    /// <para>XML 存储几何信息未知、未附加通道，或上报的 RPMB 区域为空。</para></exception>
    /// <exception cref="MtkProtocolException">A message, result frame or data frame violated the protocol; the family faults without retry.
    /// <para>某条消息、结果帧或数据帧违反协议；该协议族进入 fault 状态且不重试。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired; the family faults.
    /// <para>交换截止时间到期；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkRpmbReadResult> ReadXmlRpmbFramesAsync(MtkRpmbFrameRange range,
        MtkRpmbOptions? options = null, CancellationToken cancellationToken = default)
    {
        var effective = ResolveRpmbOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            cancellationToken.ThrowIfCancellationRequested();
            var target = RequireXmlRpmbTarget();
            var capacity = RequireXmlRpmbCapacity();
            range.Validate(capacity, effective.MaximumReadFrames);
            return await ReadXmlRpmbFramesOwnedAsync(range, target, capacity, effective, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Dumps the whole RPMB area the XML geometry reports.
    /// <para>转储 XML 几何信息上报的完整 RPMB 区域。</para>
    /// </summary>
    /// <remarks>
    /// This client divides the reported RPMB size by the fixed frame width and reads that many frames
    /// into <c>rpmb.bin</c>. Unlike the XFlash UFS
    /// reply, the XML geometry carries an RPMB size for both interfaces: the <c>rpmb_size</c> field for eMMC and
    /// the authenticated last LUA field for UFS.
    /// <para>本客户端用上报的 RPMB 大小除以固定帧宽度，读取相应数量的帧写入 <c>rpmb.bin</c>。与 XFlash 的 UFS 应答不同，XML 几何信息
    /// 为两种接口都上报了 RPMB 大小：eMMC 的 <c>rpmb_size</c> 字段与 UFS 需认证的最后一个 LUA 字段。</para>
    /// </remarks>
    /// <param name="options">Optional bounds and deadline; <see langword="null"/> uses the read-only defaults.
    /// <para>可选的边界与截止时间；<see langword="null"/> 使用只读默认值。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The delivered frames as one opaque blob plus the reported capacity.
    /// <para>以不透明数据块交付的帧，以及上报的容量。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// The geometry is unknown, the RPMB region is absent, or its size is not an exact frame multiple.
    /// <para>几何信息未知、RPMB 区域缺失，或其大小不是帧宽度的整数倍。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The area needs more frames than <see cref="MtkRpmbOptions.MaximumReadFrames"/>.
    /// <para>该区域所需帧数超过 <see cref="MtkRpmbOptions.MaximumReadFrames"/>。</para>
    /// </exception>
    /// <exception cref="MtkProtocolException">A message violated the protocol; the family faults.
    /// <para>某条消息违反协议；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired; the family faults.
    /// <para>交换截止时间到期；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkRpmbDeviceBlob> ReadXmlRpmbDeviceBlobAsync(MtkRpmbOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = ResolveRpmbOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            cancellationToken.ThrowIfCancellationRequested();
            var target = RequireXmlRpmbTarget();
            var capacity = RequireXmlRpmbCapacity();
            if (capacity % MtkRpmbWire.FrameLength != 0 || capacity / MtkRpmbWire.FrameLength > int.MaxValue)
            {
                throw new InvalidOperationException("The reported RPMB capacity is not an exact frame multiple.");
            }
            var range = new MtkRpmbFrameRange(0, (int)(capacity / MtkRpmbWire.FrameLength));
            range.Validate(capacity, effective.MaximumReadFrames);
            var read = await ReadXmlRpmbFramesOwnedAsync(range, target, capacity, effective, cancellationToken).ConfigureAwait(false);
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

    private MtkRpmbTarget RequireXmlRpmbTarget()
    {
        var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
        return storage.Kind == MtkXmlStorageKind.Emmc ? MtkRpmbTarget.Emmc : MtkRpmbTarget.Ufs;
    }

    private long RequireXmlRpmbCapacity()
    {
        var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
        var region = storage.Regions.FirstOrDefault(region => region.RequiresAuthenticatedAccess)
            ?? throw new InvalidOperationException("The reported XML geometry has no RPMB region.");
        return region.CapacityBytes;
    }

    private async Task<MtkRpmbReadResult> ReadXmlRpmbFramesOwnedAsync(MtkRpmbFrameRange range, MtkRpmbTarget target,
        long capacity, MtkRpmbOptions options, CancellationToken cancellationToken)
    {
        var frames = new List<MtkRpmbFrameResult>();
        await RunRpmbExchangeAsync(async token =>
        {
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            await BeginXmlRpmbReadAsync(channel, range.StartFrame, range.FrameCount, token).ConfigureAwait(false);
            for (var index = 0; index < range.FrameCount; index++)
            {
                var frame = checked(range.StartFrame + index);
                var (outcome, data) = await ReadXmlRpmbFrameAsync(channel, frame, token).ConfigureAwait(false);
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

            await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        }, options.OperationTimeoutMs, FaultXml, cancellationToken).ConfigureAwait(false);
        var accepted = frames.Count(frame => frame.Outcome.IsOkay);
        RecordRpmbEvidence("rpmb-read", accepted == range.FrameCount ? CommandOutcomeKind.Completed : CommandOutcomeKind.Rejected,
            $"xml {XmlReadRpmbCommand} frames {range.StartFrame}+{range.FrameCount}; {accepted} accepted.");
        return new MtkRpmbReadResult
        {
            Target = target,
            Requested = range,
            Frames = frames,
            RawBytes = JoinRpmbFrameBytes(frames),
            ReportedRpmbCapacityBytes = capacity
        };
    }

    private async Task<(MtkRpmbFrameOutcome Outcome, byte[] Data)> ReadXmlRpmbFrameAsync(MtkXmlChannel channel,
        long frame, CancellationToken token)
    {
        var reply = await channel.ReceivePayloadAsync(MtkRpmbWire.FrameLength, token).ConfigureAwait(false);
        if (reply.Length == MtkRpmbWire.FrameLength)
        {
            return (new MtkRpmbFrameOutcome(0, MtkRpmbFrameStatus.Okay), reply);
        }

        var outcome = ReadRpmbFrameOutcome(reply);
        RecordRpmbEvidence("rpmb-frame", CommandOutcomeKind.Rejected,
            $"xml {XmlReadRpmbCommand} frame {frame} reported 0x{outcome.ReportedCode:X4} in a {reply.Length}-byte frame.");
        return (outcome, []);
    }

    private async Task BeginXmlRpmbReadAsync(MtkXmlChannel channel, long sector, int sectors, CancellationToken token)
    {
        await channel.SendCommandAsync(new XElement("da", new XElement("version", "1.0"),
            new XElement("command", XmlReadRpmbCommand)), token).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
        await channel.SendStreamAsync(new MemoryStream(CreateRpmbFrameWord(sector), writable: false), 4, token)
            .ConfigureAwait(false);
        await channel.SendStreamAsync(new MemoryStream(CreateRpmbFrameWord(sectors), writable: false), 4, token)
            .ConfigureAwait(false);
    }
}