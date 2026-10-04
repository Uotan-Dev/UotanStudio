using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Sahara handshake entry points: HELLO exchange, version negotiation, READ_DATA upload loop and DONE/DONE_RESPONSE completion.
/// <para>Sahara 握手入口：HELLO 交换、版本协商、READ_DATA 上传循环以及 DONE/DONE_RESPONSE 完成。</para>
/// </summary>
public sealed partial class SaharaProtocol
{
    /// <summary>
    /// Performs the Sahara handshake, uploading the specified programmer image.
    /// <para>执行 Sahara 握手，上传指定的 programmer 镜像。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream; the caller retains ownership and the stream must be readable and seekable. When <see langword="null"/>, the handshake expects the device to already be running Firehose.<para>可选的 programmer 镜像流；调用方保留所有权，流必须可读并可定位。为 <see langword="null"/> 时握手期望设备已在运行 Firehose。</para></param>
    /// <param name="imageId">The image identifier used as the Sahara <c>ImageId</c> for the upload.<para>用作 Sahara <c>ImageId</c> 的镜像标识符。</para></param>
    /// <param name="cancellationToken">Cancellation token checked between Sahara packet boundaries.<para>在 Sahara 数据包边界之间检查的取消令牌。</para></param>
    /// <returns>The negotiated Sahara mode.<para>协商的 Sahara 模式。</para></returns>
    /// <exception cref="EdlException">Version negotiation fails, an unknown Sahara command arrives, or a NAK status is reported.<para>版本协商失败、收到未知 Sahara 命令或返回 NAK 状态。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol instance has been disposed.<para>协议实例已被释放。</para></exception>
    public async Task<SaharaMode> HandshakeAsync(Stream? programmerImage, SaharaImageId imageId = SaharaImageId.Firehose, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var images = programmerImage != null
            ? new Dictionary<uint, Stream> { [(uint)imageId] = programmerImage }
            : [];
        return await HandshakeAsync(images, cancellationToken);
    }

    /// <summary>
    /// Performs the Sahara handshake with a set of images.
    /// <para>使用一组镜像执行 Sahara 握手。</para>
    /// </summary>
    /// <param name="images">Images keyed by the Sahara <c>ImageId</c> the device will request. Callers retain ownership of each stream; every stream must be readable and seekable, and must not be mutated during the handshake.<para>以设备将请求的 Sahara <c>ImageId</c> 为键的镜像。调用方保留每个流的所有权；所有流必须可读、可定位，并且在握手期间不得被修改。</para></param>
    /// <param name="cancellationToken">Cancellation token checked between Sahara packet boundaries.<para>在 Sahara 数据包边界之间检查的取消令牌。</para></param>
    /// <returns>The negotiated Sahara mode. When the device is already running Firehose, returns <see cref="SaharaMode.ImageTxComplete"/> without uploading any image. When no image is provided, <see cref="AllowStreamDload"/> is enabled and the HELLO reports <see cref="SaharaMode.Command"/>, the HELLO is answered in command mode and <see cref="SaharaMode.Command"/> is returned.<para>协商的 Sahara 模式。设备已在运行 Firehose 时直接返回 <see cref="SaharaMode.ImageTxComplete"/> 且不上传任何镜像。未提供镜像、<see cref="AllowStreamDload"/> 已启用且 HELLO 报告 <see cref="SaharaMode.Command"/> 时，以命令模式应答 HELLO 并返回 <see cref="SaharaMode.Command"/>。</para></returns>
    /// <exception cref="EdlException">No image is provided for a device that is still in Sahara mode and a stream DLOAD switch was not selected, the HELLO version range does not intersect the host range, an unknown image id or oversized 64-bit range is requested, or a NAK status is reported.<para>设备仍处于 Sahara 模式但未提供镜像且未选择流式 DLOAD 切换、HELLO 版本区间与主机不相交、请求了未知镜像 id 或超大的 64 位区间，或返回 NAK 状态。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol instance has been disposed.<para>协议实例已被释放。</para></exception>
    public async Task<SaharaMode> HandshakeAsync(IReadOnlyDictionary<uint, Stream> images, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(images);
        DeviceIdentity = null;
        foreach (var image in images.Values)
        {
            if (image is null || !image.CanRead || !image.CanSeek)
            {
                throw new ArgumentException("Sahara images must be readable and seekable.", nameof(images));
            }
        }
        Log("Waiting for Sahara HELLO from device...");

        var helloProbeTimeout = Math.Min(_timeoutMs, EdlConstants.HelloProbeTimeoutMs);
        byte[] firstData;

        try
        {
            firstData = await ReadExactAsync(Marshal.SizeOf<SaharaHelloPacket>(), helloProbeTimeout, cancellationToken);
        }
        catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
        {
            if (QudHelloRecoveryEnabled)
            {
                Log("First read timed out; the QUD driver may have consumed the HELLO. " +
                    "Sending unsolicited HELLO_RESPONSE and waiting for the device to re-send HELLO...");
                var probeHello = new SaharaHelloResponsePacket
                {
                    Command = SaharaCommand.HelloResponse,
                    Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
                    Version = EdlConstants.SaharaVersion,
                    MinVersion = EdlConstants.SaharaHostCompatibleVersion,
                    Status = 0,
                    Mode = SaharaMode.ImageTxPending
                };
                await WriteStructAsync(probeHello, cancellationToken);
                try
                {
                    firstData = await ReadExactAsync(Marshal.SizeOf<SaharaHelloPacket>(), _timeoutMs, cancellationToken);
                }
                catch (EdlException recoveryEx) when (recoveryEx.ErrorCode == EdlErrorCode.ReadPortTimeout)
                {
                    if (await ProbeFirehoseNopAsync(cancellationToken))
                    {
                        RequireIdentityNotRequested();
                        FirehoseAlreadyRunning = true;
                        Log("Device is already running Firehose; skipping Sahara handshake.");
                        return SaharaMode.ImageTxComplete;
                    }
                    throw;
                }
            }
            else if (await ProbeFirehoseNopAsync(cancellationToken))
            {
                RequireIdentityNotRequested();
                FirehoseAlreadyRunning = true;
                Log("Device is already running Firehose; skipping Sahara handshake.");
                return SaharaMode.ImageTxComplete;
            }
            else
            {
                throw;
            }
        }

        if (LooksLikeFirehoseXml(firstData))
        {
            RequireIdentityNotRequested();
            FirehoseAlreadyRunning = true;
            Log("Device is already running Firehose; skipping Sahara handshake.");
            return SaharaMode.ImageTxComplete;
        }

        var helloPacket = BytesToStruct<SaharaHelloPacket>(firstData);
        if (images.Count == 0)
        {
            if (AllowStreamDload && helloPacket.Mode == SaharaMode.Command)
            {
                ValidateHello(helloPacket);
                NegotiatedProtocolVersion = Math.Min(helloPacket.Version, EdlConstants.SaharaHostMaxProtocolVersion);
                await WriteStructAsync(new SaharaHelloResponsePacket
                {
                    Command = SaharaCommand.HelloResponse,
                    Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
                    Version = NegotiatedProtocolVersion.Value,
                    MinVersion = EdlConstants.SaharaHostCompatibleVersion,
                    Mode = SaharaMode.Command
                }, cancellationToken).ConfigureAwait(false);
                var commandReady = BytesToStruct<SaharaPacketHeader>(
                    await ReadExactAsync(8, _timeoutMs, cancellationToken).ConfigureAwait(false));
                ValidateCommand(commandReady.Command, SaharaCommand.CommandReady, commandReady.Length, 8);
                Log("Device answered the command-mode HELLO; Sahara is in command mode for a stream DLOAD switch.");
                HandshakeComplete?.Invoke(this, SaharaMode.Command);
                return SaharaMode.Command;
            }

            throw new SaharaException("Device is in Sahara mode but no programmer image was provided.");
        }

        if (QueryIdentityBeforeUpload)
        {
            helloPacket = await QueryIdentityOwnedAsync(helloPacket, cancellationToken).ConfigureAwait(false);
        }
        var mode = helloPacket.Mode;
        await AcknowledgeImageHelloAsync(helloPacket, cancellationToken).ConfigureAwait(false);

        Log("Starting image transfer...");
        var handshakeComplete = false;
        var waitingForDone = false;
        var waitingForHello = false;
        var helloCount = 1;

        while (!handshakeComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerData = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken);
            var header = BytesToStruct<SaharaPacketHeader>(headerData);
            if ((waitingForDone && header.Command != SaharaCommand.DoneResponse)
                || (waitingForHello && header.Command != SaharaCommand.Hello))
            {
                throw new SaharaException($"Unexpected Sahara command {header.Command} during image completion.");
            }

            switch (header.Command)
            {
                case SaharaCommand.ReadData:
                    {
                        var fullData = await ReadRemainingPacket<SaharaReadDataPacket>(headerData, cancellationToken);
                        var rp = BytesToStruct<SaharaReadDataPacket>(fullData);
                        Log($"READ_DATA: ImageId={rp.ImageId}, Offset={rp.DataOffset}, Length={rp.DataLength}");
                        if (!images.TryGetValue(rp.ImageId, out var img))
                        {
                            throw new SaharaException($"Unknown image id {rp.ImageId}");
                        }

                        ValidateReadRange(rp.DataOffset, rp.DataLength, img.Length, "READ_DATA");
                        img.Position = rp.DataOffset;
                        await TransferImageRangeAsync(img, rp.DataLength, "READ_DATA", cancellationToken);
                        break;
                    }

                case SaharaCommand.ReadData64:
                    {
                        var full64 = await ReadRemainingPacket<SaharaReadData64Packet>(headerData, cancellationToken);
                        var rp64 = BytesToStruct<SaharaReadData64Packet>(full64);
                        Log($"READ_DATA64: ImageId={rp64.ImageId}, Offset=0x{rp64.DataOffset:X16}, Length=0x{rp64.DataLength:X16}");
                        if (rp64.ImageId > uint.MaxValue || !images.TryGetValue((uint)rp64.ImageId, out var img64))
                        {
                            throw new SaharaException($"Unknown image id {rp64.ImageId}");
                        }

                        if (rp64.DataOffset > long.MaxValue || rp64.DataLength > long.MaxValue)
                        {
                            throw new SaharaException("READ_DATA64 request exceeds supported stream range.");
                        }

                        ValidateReadRange((long)rp64.DataOffset, (long)rp64.DataLength, img64.Length, "READ_DATA64");
                        img64.Position = (long)rp64.DataOffset;
                        await TransferImageRangeAsync(img64, (long)rp64.DataLength, "READ_DATA64", cancellationToken);
                        break;
                    }

                case SaharaCommand.EndOfImage:
                    {
                        var fullEoi = await ReadRemainingPacket<SaharaEndOfImagePacket>(headerData, cancellationToken);
                        var eoi = BytesToStruct<SaharaEndOfImagePacket>(fullEoi);
                        Log($"END_OF_IMAGE: ImageId={eoi.ImageId}, Status={eoi.Status}");
                        if (eoi.Status != 0)
                        {
                            await TryAbandonImageTransferAsync(cancellationToken).ConfigureAwait(false);
                            throw new SaharaException($"END_OF_IMAGE with non-zero status: {eoi.Status} ({DescribeSaharaStatus(eoi.Status)})");
                        }
                        if (!images.ContainsKey(eoi.ImageId))
                        {
                            throw new SaharaException($"Unknown completed image id {eoi.ImageId}.");
                        }

                        var done = new SaharaDonePacket { Command = SaharaCommand.Done, Length = (uint)Marshal.SizeOf<SaharaDonePacket>() };
                        await WriteStructAsync(done, cancellationToken);
                        waitingForDone = true;
                        Log("Sent DONE, image transfer complete.");
                        break;
                    }

                case SaharaCommand.DoneResponse:
                    {
                        var fullDone = await ReadRemainingPacket<SaharaDoneResponsePacket>(headerData, cancellationToken);
                        var dr = BytesToStruct<SaharaDoneResponsePacket>(fullDone);
                        Log($"DONE_RESPONSE: Status={dr.Status}");
                        if (!waitingForDone)
                        {
                            throw new SaharaException("DONE_RESPONSE arrived before END_OF_IMAGE.");
                        }
                        waitingForDone = false;
                        if (dr.Status == (uint)SaharaMode.ImageTxComplete
                            || (dr.Status == (uint)SaharaMode.ImageTxPending && AllowSingleFirehosePendingCompletion
                                && images.Count == 1 && images.ContainsKey((uint)SaharaImageId.Firehose)))
                        {
                            handshakeComplete = true;
                            break;
                        }
                        if (dr.Status == (uint)SaharaMode.ImageTxPending)
                        {
                            waitingForHello = true;
                            break;
                        }
                        throw new SaharaException($"Unexpected DONE_RESPONSE status: {dr.Status} ({DescribeSaharaStatus(dr.Status)})");
                    }

                case SaharaCommand.Hello:
                    if (++helloCount > 64)
                    {
                        throw new SaharaException("Too many Sahara HELLO exchanges.");
                    }
                    var nextHello = await ReadRemainingPacket<SaharaHelloPacket>(headerData, cancellationToken).ConfigureAwait(false);
                    await AcknowledgeImageHelloAsync(BytesToStruct<SaharaHelloPacket>(nextHello), cancellationToken).ConfigureAwait(false);
                    waitingForHello = false;
                    break;
                default:
                    throw DescribeUnsupportedSaharaCommand((uint)header.Command);
            }
        }

        HandshakeComplete?.Invoke(this, mode);
        return mode;
    }

    /// <summary>
    /// Builds the failure for commands the host deliberately does not implement, so a
    /// protocol 3.0 target reports a specific reason instead of an unknown-command error.
    /// <para>为主机刻意不实现的命令构造失败信息，使协议 3.0 目标报告具体原因而不是未知命令错误。</para>
    /// </summary>
    /// <remarks>
    /// No public reference implementation (linux-msm/qdl, bkerler/edl) performs the version
    /// 3.0 WRITE_DATA image push, so the transfer layout is not transcribed from evidence and
    /// is refused rather than approximated.
    /// <para>没有公开参考实现（linux-msm/qdl、bkerler/edl）执行版本 3.0 的 WRITE_DATA 镜像推送，
    /// 其传输布局缺乏可转录的证据，因此拒绝而不是近似实现。</para>
    /// </remarks>
    /// <param name="command">The command code reported by the device.
    /// <para>设备报告的命令码。</para></param>
    /// <returns>The exception to surface to the caller.
    /// <para>向调用方抛出的异常。</para></returns>
    private static SaharaException DescribeUnsupportedSaharaCommand(uint command) => command switch
    {
        (uint)SaharaCommand.ResetState => new SaharaException(
            "The device sent RESET_STATE (protocol version 2.9); the host implements image transfer only."),
        (uint)SaharaCommand.WriteData => new SaharaException(
            "The device requested the protocol version 3.0 WRITE_DATA image push, which has no public reference implementation and is not supported by this host."),
        _ => new SaharaException($"Unexpected Sahara command: {command}")
    };

    private Task AcknowledgeImageHelloAsync(SaharaHelloPacket hello, CancellationToken token)
    {
        ValidateHello(hello);
        if (hello.Mode != SaharaMode.ImageTxPending)
        {
            throw new SaharaException($"Image transfer requires ImageTxPending, received {hello.Mode}.");
        }
        NegotiatedProtocolVersion = Math.Min(hello.Version, EdlConstants.SaharaHostMaxProtocolVersion);
        return WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = NegotiatedProtocolVersion.Value,
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Mode = SaharaMode.ImageTxPending
        }, token);
    }

    /// <summary>
    /// Sends a best-effort DONE after the target rejected an image, so the device leaves the
    /// image-transfer state instead of waiting for a host that already failed. Every cleanup failure
    /// is logged and swallowed because the caller must surface the original Sahara error unchanged.
    /// <para>在目标设备拒绝某个镜像后尽力发送 DONE，使设备离开镜像传输状态，而不是继续等待一个
    /// 已经失败的主机。所有清理失败都只记录并被吞掉，因为调用方必须原样抛出原始的 Sahara 错误。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    private async Task TryAbandonImageTransferAsync(CancellationToken cancellationToken)
    {
        try
        {
            var done = new SaharaDonePacket { Command = SaharaCommand.Done, Length = (uint)Marshal.SizeOf<SaharaDonePacket>() };
            await WriteStructAsync(done, cancellationToken).ConfigureAwait(false);
            Log("Sent DONE to abandon the rejected image transfer.");
        }
        catch (Exception ex)
        {
            Log($"Cleanup DONE after a rejected image transfer failed: {ex.Message}");
        }
    }

    private static void ValidateHello(SaharaHelloPacket hello)
    {
        ValidateCommand(hello.Command, SaharaCommand.Hello, hello.Length, (uint)Marshal.SizeOf<SaharaHelloPacket>());
        if (hello.MinVersion > EdlConstants.SaharaHostMaxProtocolVersion)
        {
            throw new SaharaException(
                $"Device requires min protocol version {hello.MinVersion}, host supports {EdlConstants.SaharaHostMaxProtocolVersion}.");
        }
        if (hello.Version < EdlConstants.SaharaMinVersion || hello.MinVersion > hello.Version)
        {
            throw new SaharaException("Invalid Sahara protocol version range.");
        }
    }

    /// <summary>
    /// Maps common Sahara NAK status codes to descriptions (bkerler/edl sahara_defs.py).
    /// <para>将常见 Sahara NAK 状态码映射为描述（bkerler/edl 的 sahara_defs.py）。</para>
    /// </summary>
    private static string DescribeSaharaStatus(uint status) => status switch
    {
        0x01 => "invalid command received in current state",
        0x02 => "protocol mismatch between host and target",
        0x03 => "invalid target protocol version",
        0x04 => "invalid host protocol version",
        0x05 => "invalid packet size",
        0x06 => "unexpected packet id",
        0x07 => "invalid image received",
        0x08 => "invalid image transmission status",
        0x09 => "invalid RAM address",
        0x0A => "invalid data offset",
        0x0B => "invalid data length",
        0x0C => "invalid image id",
        _ => "unmapped Sahara status"
    };

    /// <summary>
    /// Probes whether the device is already running Firehose by sending a NOP.
    /// <para>通过发送 NOP 探测设备是否已在运行 Firehose。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns><c>true</c> if Firehose is detected. 如果检测到 Firehose 则返回 <c>true</c>。</returns>
    private async Task<bool> ProbeFirehoseNopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nopBytes = Encoding.UTF8.GetBytes(EdlConstants.XmlHeader + EdlConstants.DataOpen + "<nop value=\"ping\"/>\n" + EdlConstants.DataClose);
            await WriteDataAsync(nopBytes, cancellationToken);

            var document = new byte[64 * 1024];
            var length = 0;
            var total = 0;
            var start = Environment.TickCount64;
            var budget = Math.Max(_timeoutMs, EdlConstants.ProbeReadBudgetMs);
            while ((Environment.TickCount64 - start) < budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingMs = (int)Math.Max(1, budget - (Environment.TickCount64 - start));
                var chunk = await ReadChunkAsync(EdlConstants.ProbeReadChunkBytes, Math.Min(_timeoutMs, remainingMs), cancellationToken);
                if (chunk.Length == 0)
                {
                    break;
                }

                foreach (var value in chunk)
                {
                    if (++total > document.Length)
                    {
                        throw new SaharaException("Firehose probe exceeds its response limit.");
                    }
                    if (length == 0 && value is 0 or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
                    {
                        continue;
                    }
                    document[length++] = value;
                    if (document.AsSpan(0, length).EndsWith("</data>"u8))
                    {
                        var block = FirehoseResponseParser.ParseDataBlock(Encoding.UTF8.GetString(document, 0, length));
                        if (block.Response is { } response)
                        {
                            return response.IsAck && !response.IsRawMode;
                        }
                        length = 0;
                    }
                }
            }
            return false;
        }
        catch (EdlException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// Checks if the data looks like a Firehose XML response.
    /// <para>检查数据是否看起来像 Firehose XML 响应。</para>
    /// </summary>
    /// <param name="d">The data to check. 要检查的数据。</param>
    /// <returns><c>true</c> if the data starts with &lt;?xml. 如果数据以 &lt;?xml 开头则返回 <c>true</c>。</returns>
    private static bool LooksLikeFirehoseXml(byte[] d) => d != null && d.Length >= 5 && d[0] == (byte)'<' && d[1] == (byte)'?' && d[2] == (byte)'x' && d[3] == (byte)'m' && d[4] == (byte)'l';
}