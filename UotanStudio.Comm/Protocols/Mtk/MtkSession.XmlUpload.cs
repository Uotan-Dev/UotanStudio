using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Uploads one explicitly selected XML DA2 image and requires the final END/START handoff.
    /// <para>上传一个显式选定的 XML DA2 镜像，并要求完成最后的 END/START 交接。</para>
    /// </summary>
    /// <remarks>
    /// The only accepted download request names this operation's opaque MEM source exactly,
    /// identifies 2nd-DA and requests CHK_NO. No target-provided path is opened. Packet
    /// acceptance is not a checksum/readback guarantee. Failure requires reconnect, not retry.
    /// The caller owns the source; signature inclusion is an explicit caller/profile choice.
    /// The successful state does not imply DA2 hardware setup, authorization or storage readiness.
    /// <para>唯一被接受的下载请求必须精确指明本次操作的不透明 MEM 源、标识 2nd-DA 并请求 CHK_NO。
    /// 不会打开任何由目标提供的路径。接受数据包并不代表校验和或回读的保证。失败时需要重连，而不是重试。
    /// 源由调用方持有；是否包含签名由调用方或设备配置显式决定。成功状态并不意味着 DA2 硬件已就绪、已授权或存储可访问。</para>
    /// </remarks>
    /// <param name="image">
    /// Readable source positioned at the selected DA2 bytes.
    /// <para>定位到选定 DA2 字节处、可读的源。</para>
    /// </param>
    /// <param name="loadAddress">
    /// Aligned target address used for both load and jump.
    /// <para>同时用于加载和跳转的对齐目标地址。</para>
    /// </param>
    /// <param name="length">
    /// Positive selected byte count, at most 64 MiB.
    /// <para>正数的选定字节数，最大 64 MiB。</para>
    /// </param>
    /// <param name="timeoutMs">
    /// Cooperative whole-operation deadline, one millisecond through 24 hours.
    /// <para>整个操作的协作式截止时间，从 1 毫秒到 24 小时。</para>
    /// </param>
    /// <param name="progress">
    /// Cumulative packet-acknowledged bytes; full progress is not final handoff success.
    /// <para>累计已被确认的数据包字节数；进度跑完不代表最终交接成功。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for waiting, capability negotiation and upload.
    /// <para>用于等待、能力协商和上传的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the DA2 upload and the final END/START handoff succeed.
    /// <para>在 DA2 上传与最终 END/START 交接均成功之后完成的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The load address is unaligned, its range overflows, or the timeout is out of bounds.
    /// <para>加载地址未对齐、其范围溢出，或超时值越界。</para></exception>
    /// <exception cref="TimeoutException">The cooperative DA2 exchange deadline expired.
    /// <para>协作式 DA2 交换截止时间已到期。</para></exception>
    public async Task UploadXmlStageTwoAsync(Stream image, ulong loadAddress, int length, int timeoutMs = 120000,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        MtkXFlashChannel.ValidateSource(image, length);
        ValidateXmlSetupTimeout(timeoutMs);
        if ((loadAddress & 3) != 0 || loadAddress > ulong.MaxValue - (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(loadAddress), "The DA2 address must be aligned and its range must not overflow.");
        }
        var sourceId = $"MEM://0x0:0x{length:x}";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:BOOT-TO"),
            new XElement("arg", new XElement("at_address", $"0x{loadAddress:x}"),
                new XElement("jmp_address", $"0x{loadAddress:x}"), new XElement("source_file", sourceId)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlHardwareInitialized);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                var packetLength = ValidateXmlStageTwoRequest(request, sourceId, length);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await SendXmlSourceOwnedAsync(channel, image, length, packetLength, progress, token).ConfigureAwait(false);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _state = MtkSessionState.XmlStageTwoStarted;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                CaptureUploadFailure(error);
                FaultXml();
                throw new TimeoutException("The XML DA2 exchange deadline expired; do not replay the upload.", error);
            }
            catch (Exception error)
            {
                CaptureUploadFailure(error);
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private static int ValidateXmlStageTwoRequest(MtkXmlMessage request, string sourceId, int length)
    {
        var packetLength = ValidateXmlDownloadRequest(request, sourceId, "2nd-DA");
        if ((length - 1) / packetLength + 1 > MaximumXmlSourcePackets)
        {
            throw new MtkProtocolException("The XML DA2 request exceeds the bounded packet count.");
        }
        return packetLength;
    }

    private static int ValidateXmlDownloadRequest(MtkXmlMessage request, string sourceId, string? expectedInfo = null)
    {
        RequireXmlCommand(request, "CMD:DOWNLOAD-FILE");
        var arguments = request.Arguments;
        if (arguments.Count != 4
            || !arguments.TryGetValue("checksum", out var checksum) || checksum != "CHK_NO"
            || !arguments.TryGetValue("info", out var info) || info.Length == 0 || (expectedInfo is not null && info != expectedInfo)
            || !arguments.TryGetValue("source_file", out var source) || source != sourceId
            || !arguments.TryGetValue("packet_length", out var packet)
            || !TryXmlHexLength(packet, MtkXmlChannel.MaximumBinaryPayload, out var packetLength))
        {
            throw new MtkProtocolException("The XML download request does not match the selected source or bounded transfer profile.");
        }
        return packetLength;
    }

    private static async Task RequireXmlOkAsync(MtkXmlChannel channel, CancellationToken token)
    {
        if (await channel.ReceiveTextAsync(token).ConfigureAwait(false) != "OK")
        {
            throw new MtkProtocolException("The XML DA did not acknowledge the transfer phase.");
        }
    }
}