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
    /// Largest declared packet length accepted while classifying the first packet, covering every
    /// Sahara packet this host dispatches (48-byte HELLO is the largest) with margin.
    /// <para>分类首包时接受的最大声明包长，覆盖本主机分发的所有 Sahara 包（最大为 48 字节的 HELLO）并留有余量。</para>
    /// </summary>
    private const int FirstPacketLengthBoundBytes = 64;

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
        byte[] firstHeader;
        try
        {
            firstHeader = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), helloProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
        {
            if (QudHelloRecoveryEnabled)
            {
                Log("First read timed out; the QUD driver may have consumed the HELLO. " +
                    "Sending a speculative HELLO_RESPONSE so the target proceeds...");
                await WriteStructAsync(BuildSpeculativeHelloResponse(images.Count > 0), cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    firstHeader = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), helloProbeTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (EdlException recoveryEx) when (recoveryEx.ErrorCode == EdlErrorCode.ReadPortTimeout)
                {
                    if (await ProbeFirehoseNopAsync(cancellationToken).ConfigureAwait(false))
                    {
                        RequireIdentityNotRequested();
                        FirehoseAlreadyRunning = true;
                        Log("Device is already running Firehose; skipping Sahara handshake.");
                        return SaharaMode.ImageTxComplete;
                    }

                    Log("Sahara HELLO recovery failed; reporting the original read timeout.");
                    throw;
                }
            }
            else if (await ProbeFirehoseNopAsync(cancellationToken).ConfigureAwait(false))
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

        if (LooksLikeFirehoseXml(firstHeader))
        {
            RequireIdentityNotRequested();
            FirehoseAlreadyRunning = true;
            Log("Device is already running Firehose; skipping Sahara handshake.");
            return SaharaMode.ImageTxComplete;
        }

        var firstPacket = BytesToStruct<SaharaPacketHeader>(firstHeader);
        if (firstPacket.Command == SaharaCommand.Hello
            && firstPacket.Length != (uint)Marshal.SizeOf<SaharaHelloPacket>())
        {
            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
            throw new SaharaException(
                $"Unexpected first-packet HELLO length {firstPacket.Length}, expected {Marshal.SizeOf<SaharaHelloPacket>()}.");
        }

        byte[]? pendingPacket;
        SaharaHelloPending? pendingHello = null;
        if (firstPacket.Command == SaharaCommand.Hello)
        {
            var helloData = await ReadRemainingPacket<SaharaHelloPacket>(firstHeader, cancellationToken).ConfigureAwait(false);
            pendingHello = new SaharaHelloPending(BytesToStruct<SaharaHelloPacket>(helloData));
            pendingPacket = null;
        }
        else if (firstPacket.Command is SaharaCommand.ReadData or SaharaCommand.ReadData64
            or SaharaCommand.EndOfImage or SaharaCommand.DoneResponse)
        {
            if (firstPacket.Length < (uint)Marshal.SizeOf<SaharaPacketHeader>()
                || firstPacket.Length > FirstPacketLengthBoundBytes)
            {
                await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
                throw new SaharaException(
                    $"Unexpected first-packet length {firstPacket.Length} for {firstPacket.Command}.");
            }

            pendingPacket = await ReadRemainingKnownLengthAsync(firstPacket.Length, firstHeader, cancellationToken)
                .ConfigureAwait(false);
            Log($"Sahara transfer started with {firstPacket.Command} instead of HELLO; continuing from that packet.");
        }
        else
        {
            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
            throw DescribeUnsupportedSaharaCommand((uint)firstPacket.Command);
        }

        if (images.Count == 0)
        {
            if (pendingPacket is not null)
            {
                throw new SaharaException("Device is in Sahara mode but no programmer image was provided.");
            }

            var streamHello = pendingHello!.Value.Packet;
            if (AllowStreamDload && streamHello.Mode == SaharaMode.Command)
            {
                ValidateHello(streamHello);
                NegotiatedProtocolVersion = Math.Min(streamHello.Version, EdlConstants.SaharaHostMaxProtocolVersion);
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

        SaharaHelloPacket helloPacket;
        var mode = SaharaMode.ImageTxPending;
        if (pendingHello is { } hello)
        {
            helloPacket = hello.Packet;
        }
        else
        {
            helloPacket = new SaharaHelloPacket
            {
                Command = SaharaCommand.Hello,
                Length = (uint)Marshal.SizeOf<SaharaHelloPacket>(),
                Version = EdlConstants.SaharaVersion,
                MinVersion = EdlConstants.SaharaMinVersion,
                Mode = SaharaMode.ImageTxPending
            };
            Log("The target skipped HELLO and requested image data directly; continuing the transfer.");
        }

        if (QueryIdentityBeforeUpload && pendingHello is null)
        {
            throw new SaharaException("A target that skipped HELLO cannot provide the required Sahara device identity.");
        }
        if (QueryIdentityBeforeUpload)
        {
            helloPacket = await QueryIdentityOwnedAsync(helloPacket, cancellationToken).ConfigureAwait(false);
        }
        if (pendingHello is not null)
        {
            mode = helloPacket.Mode;
            await AcknowledgeImageHelloAsync(helloPacket, cancellationToken).ConfigureAwait(false);
        }

        Log("Starting image transfer...");
        var handshakeComplete = false;
        var waitingForDone = false;
        var waitingForHello = false;
        var helloCount = 1;
        var imagesCompleted = 0;

        while (!handshakeComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] headerData;
            if (pendingPacket is not null)
            {
                headerData = pendingPacket;
                pendingPacket = null;
            }
            else
            {
                try
                {
                    headerData = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout
                    && (waitingForDone || waitingForHello) && imagesCompleted > 0)
                {
                    Log("Read timed out while waiting for the next packet after completed image transfers; " +
                        "the device most likely switched to Firehose. Completing the handshake.");
                    handshakeComplete = true;
                    break;
                }
            }

            var header = BytesToStruct<SaharaPacketHeader>(headerData);
            if ((waitingForDone && header.Command != SaharaCommand.DoneResponse)
                || (waitingForHello && header.Command != SaharaCommand.Hello))
            {
                await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
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
                            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
                            throw new SaharaException(
                                $"The target requested image id {rp.ImageId} which the supplied programmer set does not contain.")
                            {
                                FailureKind = EdlFailureKind.ProgrammerImageMismatch
                            };
                        }

                        ValidateReadRange(rp.DataOffset, rp.DataLength, img.Length, "READ_DATA");
                        img.Position = rp.DataOffset;
                        await TransferImageRangeAsync(img, rp.DataLength, "READ_DATA", cancellationToken);
                        RaiseImageProgress(rp.ImageId, (long)rp.DataOffset + rp.DataLength, img);
                        break;
                    }

                case SaharaCommand.ReadData64:
                    {
                        var full64 = await ReadRemainingPacket<SaharaReadData64Packet>(headerData, cancellationToken);
                        var rp64 = BytesToStruct<SaharaReadData64Packet>(full64);
                        Log($"READ_DATA64: ImageId={rp64.ImageId}, Offset=0x{rp64.DataOffset:X16}, Length=0x{rp64.DataLength:X16}");
                        if (rp64.ImageId > uint.MaxValue || !images.TryGetValue((uint)rp64.ImageId, out var img64))
                        {
                            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
                            throw new SaharaException(
                                $"The target requested image id {rp64.ImageId} which the supplied programmer set does not contain.")
                            {
                                FailureKind = EdlFailureKind.ProgrammerImageMismatch
                            };
                        }

                        if (rp64.DataOffset > long.MaxValue || rp64.DataLength > long.MaxValue)
                        {
                            throw new SaharaException("READ_DATA64 request exceeds supported stream range.");
                        }

                        ValidateReadRange((long)rp64.DataOffset, (long)rp64.DataLength, img64.Length, "READ_DATA64");
                        img64.Position = (long)rp64.DataOffset;
                        await TransferImageRangeAsync(img64, (long)rp64.DataLength, "READ_DATA64", cancellationToken);
                        RaiseImageProgress((uint)rp64.ImageId, (long)rp64.DataOffset + (long)rp64.DataLength, img64);
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
                            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
                            throw new SaharaException(
                                $"The target completed image id {eoi.ImageId} which the supplied programmer set does not contain.")
                            {
                                FailureKind = EdlFailureKind.ProgrammerImageMismatch
                            };
                        }

                        imagesCompleted++;
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
                        await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
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
                    await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
                    throw DescribeUnsupportedSaharaCommand((uint)header.Command);
            }
        }

        HandshakeComplete?.Invoke(this, mode);
        return mode;
    }

    /// <summary>Buffers a HELLO parsed from the first packet so the image-less and identity paths keep
    /// their original HELLO-driven flow.<para>缓存从首包解析出的 HELLO，使无镜像与身份查询路径保持原有的
    /// HELLO 驱动流程。</para></summary>
    /// <param name="Packet">The parsed HELLO packet.<para>解析出的 HELLO 包。</para></param>
    private readonly record struct SaharaHelloPending(SaharaHelloPacket Packet);

    /// <summary>
    /// Builds the unsolicited HELLO_RESPONSE sent when the QUD serial driver consumed the bootrom HELLO:
    /// it advertises the host version and the mode the pending handshake needs, exactly like the
    /// speculative response the reference implementations send.
    /// <para>构造 QUD 串口驱动消费掉 bootrom HELLO 时主动发送的 HELLO_RESPONSE：它宣告主机版本与本次握手
    /// 所需模式，与参考实现发送的推测性应答一致。</para>
    /// </summary>
    /// <param name="imageTransfer">Whether the pending handshake expects image transfer; <see langword="false"/>
    /// selects command mode.<para>本次握手是否期望镜像传输；<see langword="false"/> 选择命令模式。</para></param>
    /// <returns>The 48-byte speculative HELLO_RESPONSE packet.<para>48 字节的推测性 HELLO_RESPONSE 包。</para></returns>
    private static SaharaHelloResponsePacket BuildSpeculativeHelloResponse(bool imageTransfer) => new()
    {
        Command = SaharaCommand.HelloResponse,
        Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
        Version = EdlConstants.SaharaVersion,
        MinVersion = EdlConstants.SaharaHostCompatibleVersion,
        Status = 0,
        Mode = imageTransfer ? SaharaMode.ImageTxPending : SaharaMode.Command
    };

    /// <summary>
    /// Reports whether the first bytes look like a Firehose document after skipping packet padding and
    /// ASCII whitespace, so a target already running Firehose is recognized from the header-sized read.
    /// <para>在跳过包填充与 ASCII 空白后，报告首字节是否形似 Firehose 文档，使头部大小的读取也能识别
    /// 已在运行 Firehose 的目标。</para>
    /// </summary>
    /// <param name="d">The data to check. <para>要检查的数据。</para></param>
    /// <returns><c>true</c> if the first non-padding bytes are &lt;?xml. <para>如果首个非填充字节起为 &lt;?xml 则返回 <c>true</c>。</para></returns>
    private static bool LooksLikeFirehoseXml(byte[] d)
    {
        if (d is null)
        {
            return false;
        }

        var start = EdlConstants.MeasureDocumentPrefix(d);
        return d.AsSpan(start).StartsWith("<?xml"u8);
    }

    /// <summary>
    /// Raises <see cref="ImageProgress"/> after one READ_DATA/READ_DATA64 block has been served.
    /// <para>在服务完一个 READ_DATA/READ_DATA64 块后触发 <see cref="ImageProgress"/>。</para>
    /// </summary>
    /// <param name="imageId">The Sahara image identifier being served.<para>正在服务的 Sahara 镜像标识。</para></param>
    /// <param name="bytesUploaded">The cumulative bytes uploaded for this image so far.<para>该镜像目前已累计上传的字节数。</para></param>
    /// <param name="image">The stream serving the image, used for its total length when seekable.<para>正在提供镜像的流；可定位时用于总长。</para></param>
    private void RaiseImageProgress(uint imageId, long bytesUploaded, Stream image)
    {
        var handler = ImageProgress;
        if (handler is null)
        {
            return;
        }

        handler.Invoke(this, new SaharaImageProgressEventArgs(imageId, bytesUploaded,
            image.CanSeek ? image.Length : null));
    }

    /// <summary>
    /// Builds the failure for commands the host deliberately does not implement, so a
    /// protocol 3.0 target reports a specific reason instead of an unknown-command error.
    /// <para>为主机刻意不实现的命令构造失败信息，使协议 3.0 目标报告具体原因而不是未知命令错误。</para>
    /// </summary>
    /// <remarks>
    /// This host does not implement the version 3.0 WRITE_DATA image push: the transfer layout has no
    /// wire description this host can follow, so the command is refused rather than approximated.
    /// <para>本主机不实现版本 3.0 的 WRITE_DATA 镜像推送：该传输布局没有本主机可遵循的线协议描述，
    /// 因此直接拒绝而不是近似实现。</para>
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

    /// <summary>
    /// Returns a target that received an unexpected packet to the state that re-announces HELLO, so the next
    /// attempt does not require replugging the device.
    /// <para>让收到意外数据包的目标回到会重新宣告 HELLO 的状态，使下一次尝试不必重新插拔设备。</para>
    /// </summary>
    /// <remarks>Every failure is logged and swallowed because the caller rethrows the original protocol error
    /// unchanged. <para>所有失败只记录并被吞掉，因为调用方必须原样抛出原始的协议错误。</para></remarks>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task completing after the best-effort reset. <para>尽力复位后完成的任务。</para></returns>
    private async Task TryResetStateMachineAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reset = BuildSaharaSimpleCommand(SaharaCommand.ResetState);
            await WriteDataAsync(reset, cancellationToken).ConfigureAwait(false);
            Log("Sent RESET_STATE_MACHINE so the target re-announces HELLO.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Best-effort RESET_STATE_MACHINE failed: {ex.Message}");
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
    /// Maps common Sahara NAK status codes to descriptions.
    /// <para>将常见 Sahara NAK 状态码映射为描述。</para>
    /// </summary>
    private static string DescribeSaharaStatus(uint status) => status switch
    {
        0x01 => "invalid command received in current state",
        0x02 => "protocol mismatch between host and target",
        0x03 => "invalid target protocol version",
        0x04 => "invalid host protocol version",
        0x05 => "invalid packet size",
        0x06 => "unexpected packet id",
        0x07 => "invalid image header size",
        0x08 => "invalid image data size",
        0x09 => "invalid image type received",
        0x0A => "invalid transmission length",
        0x0B => "invalid reception length",
        0x0C => "general transmission or reception error",
        _ => "unmapped Sahara status"
    };

    /// <summary>
    /// Probes whether the device is already running Firehose by sending a NOP.
    /// <para>通过发送 NOP 探测设备是否已在运行 Firehose。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> if Firehose is detected. <para>如果检测到 Firehose 则返回 <c>true</c>。</para></returns>
    private async Task<bool> ProbeFirehoseNopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WriteDataAsync(EdlConstants.NopProbeFrame, cancellationToken);

            var document = new byte[64 * 1024];
            var length = 0;
            var total = 0;
            var prefixPending = true;
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
                    if (length == 0 && prefixPending && EdlConstants.DocumentLeadingPadding.Contains(value))
                    {
                        continue;
                    }
                    prefixPending = false;
                    document[length++] = value;
                    if (document.AsSpan(0, length).EndsWith("</data>"u8))
                    {
                        var skip = EdlConstants.MeasureDocumentPrefix(document.AsSpan(0, length));
                        var block = FirehoseResponseParser.ParseDataBlock(
                            Encoding.UTF8.GetString(document, skip, length - skip));
                        if (block.Response is { } response)
                        {
                            return (response.IsAck || response.IsNak) && !response.IsRawMode;
                        }
                        length = 0;
                        prefixPending = true;
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
}