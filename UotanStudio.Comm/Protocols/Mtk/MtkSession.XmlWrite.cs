using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Writes exactly a selected aligned XML storage range from a caller-owned stream.
    /// <para>从调用方持有的流精确写入选定的、按块对齐的 XML 存储范围。</para></summary>
    /// <remarks>The caller must authorize this destructive operation and keep the source stable and exclusive. No padding, clamping, source-path resolution, automatic authentication, retry or rollback is performed. An interrupted exchange may have changed storage even when no progress was reported. ACKs and END/START are agent reports, not independent persistence or readback verification.
    /// <para>调用方必须授权这一破坏性操作，并保持源数据稳定且独占。不进行填充、截断、源路径解析、 自动认证、重试或回滚。被打断的交换即使未上报任何进度，也可能已经改写了存储。 ACK 与 END/START 只是代理的上报，并非独立的持久化或回读校验。</para></remarks>
    /// <param name="regionName">An exact XML physical-region token from the discovered geometry, excluding RPMB.
    /// <para>来自已发现几何信息的精确 XML 物理区域标识，不含 RPMB。</para></param>
    /// <param name="offset">Nonnegative block-aligned byte offset.<para>非负、按块对齐的字节偏移。</para></param>
    /// <param name="length">Positive block-aligned byte count bounded by reported capacity.
    /// <para>正数、按块对齐、受上报容量约束的字节数。</para></param>
    /// <param name="source">Readable source positioned at the selected bytes; remains caller-owned.
    /// <para>定位到选定字节处、可读的源；所有权仍归调用方。</para></param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整个操作的协作式截止时间，从 1 毫秒到 24 小时。</para></param>
    /// <param name="progress">Cumulative packet-acknowledged bytes; full progress is not final command completion.
    /// <para>累计已被确认的数据包字节数；进度跑完不代表命令最终完成。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the exchange; cannot roll back device changes.
    /// <para>用于等待和交换的取消令牌；无法回滚设备上的更改。</para></param>
    /// <returns>A task that completes once the write exchange and the final END/START command completion are acknowledged.
    /// <para>在写入交换与最终 END/START 命令完成均被确认之后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">Storage geometry has not been discovered or the XML DA channel is not attached.
    /// <para>尚未发现存储几何信息，或 XML DA 通道未附加。</para></exception>
    /// <exception cref="TimeoutException">The cooperative write deadline expired; storage may already have changed.
    /// <para>协作式写入截止时间已到期；存储可能已经被改写。</para></exception>
    public async Task WriteXmlStorageAsync(string regionName, long offset, long length, Stream source,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateXmlStorageSource(source, length);
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteXmlStorageOwnedAsync(regionName, offset, length, source, timeoutMs, progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task WriteXmlStorageOwnedAsync(string regionName, long offset, long length, Stream source,
        int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XmlStorageGeometryKnown);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
        storage.ValidateRange(regionName, offset, length);
        RequireXmlOrdinaryRegionSnapshot(regionName);
        ValidateXmlStorageSource(source, length);
        var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
        var sourceId = $"MEM://0x0:0x{length:x}";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:WRITE-FLASH"),
            new XElement("arg", new XElement("partition", regionName), new XElement("offset", $"0x{offset:x}"),
                new XElement("source_file", sourceId)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: true, fileSize: true), token).ConfigureAwait(false);
            await channel.SendCommandAsync(command, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            var sizeRequest = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
            RequireXmlCommand(sizeRequest, "CMD:FILE-SYS-OPERATION");
            var arguments = sizeRequest.Arguments;
            if (arguments.Count != 2
                || !arguments.TryGetValue("key", out var key) || key != "FILE-SIZE"
                || !arguments.TryGetValue("file_path", out var path) || path != sourceId)
            {
                throw new MtkProtocolException("Only FILE-SIZE for the selected opaque XML source is supported.");
            }
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            await channel.AcknowledgeValueAsync((ulong)length, token).ConfigureAwait(false);
            var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
            var packetLength = ValidateXmlDownloadRequest(request, sourceId);
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            await SendXmlSourceOwnedAsync(channel, source, length, packetLength, progress, token).ConfigureAwait(false);
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            FaultXml();
            throw new TimeoutException("The XML write deadline expired; storage may have changed and the write must not be replayed.", error);
        }
        catch
        {
            FaultXml();
            throw;
        }
    }

    private static void ValidateXmlStorageSource(Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!source.CanRead || (source.CanSeek && source.Length - source.Position < length))
        {
            throw new ArgumentException("The XML source is unreadable or shorter than the selected range.", nameof(source));
        }
    }

    private static async Task SendXmlSourceOwnedAsync(MtkXmlChannel channel, Stream source, long length,
        int packetLength, IProgress<long>? progress, CancellationToken token)
    {
        RequireBoundedXmlPacketCount(length, packetLength);
        await channel.AcknowledgeValueAsync((ulong)length, token).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
        long sent = 0;
        while (sent < length)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(packetLength, length - sent);
            await channel.AcknowledgeValueAsync(0, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            await channel.SendStreamAsync(source, count, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            sent += count;
            progress?.Report(sent);
        }
    }

    /// <summary>
    /// Bounds one device-negotiated XML file transfer by packet count before a single payload byte
    /// moves, matching the ceiling the DA2 stage already applies. The packet length is
    /// device-authored, so a tiny negotiated value cannot turn a bounded transfer into an
    /// unbounded number of acknowledgement exchanges.
    /// <para>在写出任何负载字节之前，按包数约束一次设备协商的 XML 文件传输，上限与 DA2 阶段既有上限一致。
    /// 包长由设备决定，因此极小的协商值无法把有界传输变成无限次应答交换。
    /// </para>
    /// </summary>
    /// <param name="length">The declared transfer byte count. 声明的传输字节数。</param>
    /// <param name="packetLength">The negotiated packet length in bytes. 协商出的包长（字节）。</param>
    /// <exception cref="MtkProtocolException">The negotiated packet count exceeds
    /// <see cref="MaximumXmlSourcePackets"/>. <para>协商出的包数超过
    /// <see cref="MaximumXmlSourcePackets"/>。</para></exception>
    private static void RequireBoundedXmlPacketCount(long length, int packetLength)
    {
        if ((length - 1) / packetLength + 1 > MaximumXmlSourcePackets)
        {
            throw new MtkProtocolException("The XML file transfer exceeds the bounded packet count.");
        }
    }
}