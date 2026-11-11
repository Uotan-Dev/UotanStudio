using System.Buffers.Binary;
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
    /// Read timeout used while discarding packets a failed attempt left in the endpoint, short enough to keep
    /// recovery inside the caller's retry budget.
    /// <para>丢弃失败尝试遗留在端点中的数据包时使用的读取超时，短到足以把恢复控制在调用方的重试预算内。</para>
    /// </summary>
    private const int StaleDrainTimeoutMs = 250;

    /// <summary>
    /// Upper bound on the packets discarded before a reset, so a target that keeps streaming cannot hold the
    /// recovery open.
    /// <para>复位前排空的数据包上限，使持续输出的目标无法把恢复过程拖住。</para>
    /// </summary>
    private const int StaleDrainPacketLimit = 8;

    /// <summary>
    /// Total budget spent waiting for a reset target to re-announce HELLO. It is a whole budget, not a single read:
    /// a bootrom that needs longer than one slice to service the reset still re-announces HELLO inside it.
    /// <para>等待被复位目标重新宣告 HELLO 的总预算。它是整段预算而不是单次读取：需要超过一个读取切片的
    /// bootrom 仍会在这段预算内重新宣告 HELLO。</para>
    /// </summary>
    private const int StaleRecoveryBudgetMs = EdlConstants.HelloProbeTimeoutMs * 2;

    /// <summary>
    /// Per-read slice applied inside the HELLO recovery budget; a slice that ends without a packet spends only that
    /// slice, so the budget survives until the target finally answers.
    /// <para>HELLO 恢复预算内每次读取的切片；没有数据包的切片只消耗该切片，使预算一直保留到目标应答为止。</para>
    /// </summary>
    private const int StaleRecoveryReadTimeoutMs = 500;

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
        Log($"Waiting for Sahara HELLO from device (image(s)={images.Count}, " +
            $"QudHelloRecoveryEnabled={QudHelloRecoveryEnabled})...");

        var helloProbeTimeout = Math.Min(_timeoutMs, EdlConstants.HelloProbeTimeoutMs);
        var helloWatch = System.Diagnostics.Stopwatch.StartNew();
        byte[] firstHeader;
        try
        {
            firstHeader = await ReadPacketPrefixAsync(FirstPacketLengthBoundBytes, helloProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            Log($"Read the first packet after {helloWatch.ElapsedMilliseconds} ms of a {helloProbeTimeout} ms budget.");
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
                    firstHeader = await ReadPacketPrefixAsync(FirstPacketLengthBoundBytes, helloProbeTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (EdlException recoveryEx) when (recoveryEx.ErrorCode == EdlErrorCode.ReadPortTimeout)
                {
                    if (!LinkReportsSahara && await ProbeFirehoseNopAsync(cancellationToken).ConfigureAwait(false))
                    {
                        RequireIdentityNotRequested();
                        FirehoseAlreadyRunning = true;
                        Log("Device is already running Firehose; skipping Sahara handshake.");
                        return SaharaMode.ImageTxComplete;
                    }

                    if (await TryWakeSaharaHelloAsync(cancellationToken).ConfigureAwait(false) is not { } wokenHeader)
                    {
                        Log("Sahara HELLO recovery failed; reporting the original read timeout.");
                        throw;
                    }

                    firstHeader = wokenHeader;
                }
            }
            else if (!LinkReportsSahara && await ProbeFirehoseNopAsync(cancellationToken).ConfigureAwait(false))
            {
                RequireIdentityNotRequested();
                FirehoseAlreadyRunning = true;
                Log("Device is already running Firehose; skipping Sahara handshake.");
                return SaharaMode.ImageTxComplete;
            }
            else if (await TryWakeSaharaHelloAsync(cancellationToken).ConfigureAwait(false) is { } wokenFirstHeader)
            {
                firstHeader = wokenFirstHeader;
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
        Log($"First packet: command={firstPacket.Command} (0x{(uint)firstPacket.Command:X2}), length={firstPacket.Length}.");
        if (firstPacket.Command == SaharaCommand.Hello
            && firstPacket.Length != (uint)Marshal.SizeOf<SaharaHelloPacket>())
        {
            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
            throw new SaharaException(
                $"Unexpected first-packet HELLO length {firstPacket.Length}, expected {Marshal.SizeOf<SaharaHelloPacket>()}.");
        }

        SaharaFirstPacket? bootstrap = null;
        if (firstPacket.Command == SaharaCommand.Hello)
        {
            var helloData = await ReadRemainingPacket<SaharaHelloPacket>(firstHeader, cancellationToken).ConfigureAwait(false);
            var receivedHello = BytesToStruct<SaharaHelloPacket>(helloData);
            Log($"HELLO: version={receivedHello.Version}, minVersion={receivedHello.MinVersion}, " +
                $"maxCommandLength={receivedHello.MaxCommandLength}, mode={receivedHello.Mode} (0x{(uint)receivedHello.Mode:X2}).");
            bootstrap = receivedHello;
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

            var packet = await ReadRemainingKnownLengthAsync(firstPacket.Length, firstHeader, cancellationToken)
                .ConfigureAwait(false);
            if (IsStaleTransferResult(firstPacket.Command, packet))
            {
                Log($"{firstPacket.Command} at the start of the upload carries a status from an earlier, already " +
                    "finished attempt; resetting the target state machine instead of resuming that transfer.");
                if (await TryResetTargetAndAwaitHelloAsync(cancellationToken).ConfigureAwait(false) is { } recoveredHello)
                {
                    bootstrap = recoveredHello;
                }
                else
                {
                    throw DescribeStaleTransferResult(firstPacket.Command, packet);
                }
            }
            else
            {
                Log($"Sahara transfer started with {firstPacket.Command} instead of HELLO; continuing from that packet.");
                bootstrap = packet;
            }
        }
        else
        {
            await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
            throw DescribeUnsupportedSaharaCommand((uint)firstPacket.Command);
        }

        if (images.Count == 0)
        {
            if (bootstrap is not { Value: SaharaHelloPacket streamHello })
            {
                throw new SaharaException("Device is in Sahara mode but no programmer image was provided.");
            }

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
        var helloAnnounced = bootstrap is { Value: SaharaHelloPacket };
        if (bootstrap is { Value: SaharaHelloPacket announced })
        {
            helloPacket = announced;
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

        if (QueryIdentityBeforeUpload && !helloAnnounced)
        {
            throw new SaharaException("A target that skipped HELLO cannot provide the required Sahara device identity.");
        }
        if (QueryIdentityBeforeUpload)
        {
            helloPacket = await QueryIdentityOwnedAsync(helloPacket, cancellationToken).ConfigureAwait(false);
        }
        if (helloAnnounced)
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
        var rejectedImageRecovered = false;

        while (!handshakeComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] headerData;
            if (bootstrap is { Value: byte[] pendingPacket })
            {
                headerData = pendingPacket;
                bootstrap = null;
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
                            if (!rejectedImageRecovered)
                            {
                                rejectedImageRecovered = true;
                                Log($"The target rejected image {eoi.ImageId} with status {eoi.Status} " +
                                    $"({DescribeSaharaStatus(eoi.Status)}); resetting its state machine and announcing the image again.");
                                if (await TryResetTargetAndAwaitHelloAsync(cancellationToken).ConfigureAwait(false)
                                    is { } restartHello)
                                {
                                    imagesCompleted = 0;
                                    waitingForDone = false;
                                    waitingForHello = false;
                                    mode = restartHello.Mode;
                                    await AcknowledgeImageHelloAsync(restartHello, cancellationToken).ConfigureAwait(false);
                                    Log("The reset target re-announced HELLO; restarting the image upload from the beginning.");
                                    break;
                                }
                            }
                            else
                            {
                                await TryResetTargetStateAsync(cancellationToken).ConfigureAwait(false);
                            }

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

    private async Task AcknowledgeImageHelloAsync(SaharaHelloPacket hello, CancellationToken token)
    {
        ValidateHello(hello);
        if (hello.Mode != SaharaMode.ImageTxPending)
        {
            throw new SaharaException($"Image transfer requires ImageTxPending, received {hello.Mode}.");
        }
        NegotiatedProtocolVersion = Math.Min(hello.Version, EdlConstants.SaharaHostMaxProtocolVersion);
        Log($"Answering HELLO with HELLO_RESP: version={NegotiatedProtocolVersion.Value}, " +
            $"minVersion={EdlConstants.SaharaHostCompatibleVersion}, mode=ImageTxPending. A bootrom only keeps the " +
            "announced slot open until this reply, so a long gap here is what makes it reject the late answer.");
        await WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = NegotiatedProtocolVersion.Value,
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Mode = SaharaMode.ImageTxPending
        }, token).ConfigureAwait(false);
        Log("HELLO_RESP written; waiting for the target's first upload request.");
    }

    /// <summary>
    /// Sends the Sahara RESET packet, so a target still in emergency download leaves it and boots by itself.
    /// <para>发送 Sahara RESET 数据包，使仍在紧急下载模式的目标离开该模式并自行启动。</para>
    /// </summary>
    /// <remarks>
    /// This is the bootrom RESET (0x07), not RESET_STATE_MACHINE (0x13): it ends the Sahara exchange instead of
    /// restarting it, which is the only way to leave emergency download without a programmer upload. Packets a
    /// previous attempt left queued are discarded first so the acknowledgement is read from the reset itself.
    /// <para>这是 bootrom 的 RESET（0x07），不是 RESET_STATE_MACHINE（0x13）：它结束 Sahara 交换而不是重新开始，
    /// 也是不加载 programmer 就离开紧急下载模式的唯一方式。先丢弃上一次尝试遗留的数据包，使确认读自本次复位。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token checked before the write and the acknowledgement read.
    /// <para>在写入与读取确认之前检查的取消令牌。</para></param>
    /// <returns>A task completing once the target acknowledged the reset.
    /// <para>目标确认复位后完成的任务。</para></returns>
    /// <exception cref="SaharaException">The target answered with a packet other than RESET_RESPONSE.
    /// <para>目标以 RESET_RESPONSE 之外的数据包应答。</para></exception>
    /// <exception cref="EdlException">The acknowledgement did not arrive within the command timeout.
    /// <para>确认未在命令超时内到达。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol instance has been disposed.
    /// <para>协议实例已被释放。</para></exception>
    public async Task ResetTargetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await DrainPendingPacketsAsync(cancellationToken).ConfigureAwait(false);
        await SendSaharaResetAsync(_timeoutMs, cancellationToken).ConfigureAwait(false);
        Log("The target acknowledged the Sahara RESET and leaves emergency download mode.");
    }

    /// <summary>
    /// Reports whether a packet buffered at the start of an upload is the result of an earlier transfer rather
    /// than a request this handshake can serve, so the caller resets the target instead of answering it.
    /// <para>报告上传起点读到的数据包是上一轮传输的结果而不是本次握手可服务的请求，使调用方复位目标而不是应答它。</para>
    /// </summary>
    /// <remarks>
    /// A rejected image answers with <c>END_OF_IMAGE</c> carrying a non-zero status, and a <c>DONE_RESPONSE</c>
    /// only exists once a <c>DONE</c> was answered. Neither belongs to a transfer that has not started, and
    /// answering either one is what leaves the target in a state the next attempt cannot use.
    /// <para>被拒绝的镜像以携带非零状态的 <c>END_OF_IMAGE</c> 回应，而 <c>DONE_RESPONSE</c> 只会在 <c>DONE</c>
    /// 被应答后出现。两者都不属于尚未开始的传输，而应答其中任何一个正是让目标停留在下一次尝试无法使用的状态的原因。</para>
    /// </remarks>
    /// <param name="command">The command of the buffered packet.<para>已缓冲数据包的命令。</para></param>
    /// <param name="packet">The complete buffered packet.<para>完整的已缓冲数据包。</para></param>
    /// <returns><see langword="true"/> when the packet reports a finished or failed transfer.
    /// <para>数据包报告已结束或已失败的传输时为 <see langword="true"/>。</para></returns>
    private static bool IsStaleTransferResult(SaharaCommand command, byte[] packet) => command switch
    {
        SaharaCommand.EndOfImage => packet.Length >= Marshal.SizeOf<SaharaEndOfImagePacket>()
            && BytesToStruct<SaharaEndOfImagePacket>(packet).Status != 0,
        SaharaCommand.DoneResponse => true,
        _ => false
    };

    /// <summary>
    /// Builds the failure reported when a stale transfer result cannot be turned back into a HELLO, keeping the
    /// status text the target reported.
    /// <para>构造无法把残留的传输结果重新变回 HELLO 时上报的失败，保留目标报告的状态文本。</para>
    /// </summary>
    /// <param name="command">The command of the stale packet.<para>残留数据包的命令。</para></param>
    /// <param name="packet">The complete stale packet.<para>完整的残留数据包。</para></param>
    /// <returns>The exception to surface to the caller.<para>向调用方抛出的异常。</para></returns>
    private static SaharaException DescribeStaleTransferResult(SaharaCommand command, byte[] packet)
    {
        if (command == SaharaCommand.EndOfImage && packet.Length >= Marshal.SizeOf<SaharaEndOfImagePacket>())
        {
            var eoi = BytesToStruct<SaharaEndOfImagePacket>(packet);
            return new SaharaException(
                $"END_OF_IMAGE with non-zero status: {eoi.Status} ({DescribeSaharaStatus(eoi.Status)})");
        }

        return new SaharaException($"The target answered {command} from an earlier transfer that the host cannot resume.");
    }

    /// <summary>
    /// Discards the packets a failed attempt left in the endpoint so the following handshake starts from a clean
    /// packet boundary instead of consuming a result that belongs to the previous attempt.
    /// <para>丢弃失败尝试遗留在端点中的数据包，使随后的握手从干净的数据包边界开始，而不是消费属于上一次尝试的结果。</para>
    /// </summary>
    /// <remarks>The drain is bounded in both packets and time: the first read timeout is the silence the caller
    /// needs, and the packet ceiling stops a target that keeps streaming from holding recovery open.
    /// <para>排空在数据包数量与时间上都受限：第一次读取超时即调用方所需的静默，数据包上限则阻止持续输出的目标拖住恢复过程。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A task completing once the endpoint is quiet or the bound is reached.
    /// <para>端点静默或达到上限后完成的任务。</para></returns>
    private async Task DrainPendingPacketsAsync(CancellationToken cancellationToken)
    {
        for (var drained = 0; drained < StaleDrainPacketLimit; drained++)
        {
            try
            {
                var (header, _) = await ReceiveSaharaPacketAsync(StaleDrainTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                Log($"Discarded a Sahara {header.Command} packet left behind by an earlier transfer.");
            }
            catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.ReadPortTimeout or EdlErrorCode.TransportTimeout)
            {
                return;
            }
            catch (SaharaException ex)
            {
                Log($"Sahara drain stopped on a packet the host cannot frame: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Drains the endpoint and asks the target to reset its Sahara state machine, leaving the link in the state
    /// a later attempt can start from.
    /// <para>排空端点并要求目标复位 Sahara 状态机，使链路回到后续尝试可以开始的状态。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>A task completing after the best-effort reset.<para>尽力复位后完成的任务。</para></returns>
    private async Task TryResetTargetStateAsync(CancellationToken cancellationToken)
    {
        await DrainPendingPacketsAsync(cancellationToken).ConfigureAwait(false);
        await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wakes a bootrom that already answered its one HELLO before this handshake started and returns the header it
    /// re-announces, or <see langword="null"/> when it stays silent for the whole budget.
    /// <para>唤醒在本次握手开始前就已应答过唯一一次 HELLO 的 bootrom 并返回它重新宣告的包头；在整个预算内保持
    /// 静默时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <remarks>
    /// The bootrom announces HELLO exactly once after it enters emergency download, so a session that probed the
    /// endpoint first consumes it and leaves the later handshake with a silent link. RESET_STATE_MACHINE is the only
    /// host request that makes it announce HELLO again, and it is written only once the target is known not to be
    /// running Firehose: either <see cref="LinkReportsSahara"/> already proved the link speaks Sahara, or the
    /// Firehose probe just failed to answer. Every read keeps the remaining budget instead of ending the recovery,
    /// because a bootrom that needs longer than one slice still answers inside it.
    /// <para>bootrom 进入紧急下载模式后只会宣告一次 HELLO，因此先探测过端点的会话已经消费掉它，留给后续握手的
    /// 是静默链路。RESET_STATE_MACHINE 是让它重新宣告 HELLO 的唯一主机请求，且只在确认目标未运行 Firehose 之后
    /// 才写入：<see cref="LinkReportsSahara"/> 已经证明链路使用 Sahara，或 Firehose 探测刚刚没有得到应答。每次读取
    /// 都保留剩余预算而不是结束恢复，因为需要超过一个切片才响应的 bootrom 仍会在这段预算内应答。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The header of the re-announced packet, or <see langword="null"/> when nothing arrived.
    /// <para>重新宣告的数据包头；没有任何数据到达时为 <see langword="null"/>。</para></returns>
    private async Task<byte[]?> TryWakeSaharaHelloAsync(CancellationToken cancellationToken)
    {
        Log("The link stayed silent after both HELLO reads; the bootrom already announced its one HELLO to an " +
            "earlier probe. Writing Sahara RESET_STATE_MACHINE so it announces HELLO again.");
        await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
        var deadline = Environment.TickCount64 + StaleRecoveryBudgetMs;
        while (Environment.TickCount64 < deadline)
        {
            var remainingMs = (int)Math.Max(1, deadline - Environment.TickCount64);
            try
            {
                return await ReadPacketPrefixAsync(FirstPacketLengthBoundBytes,
                    Math.Min(remainingMs, StaleRecoveryReadTimeoutMs), cancellationToken).ConfigureAwait(false);
            }
            catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.ReadPortTimeout or EdlErrorCode.TransportTimeout)
            {
                Log($"No packet within {Math.Min(remainingMs, StaleRecoveryReadTimeoutMs)} ms of the wake budget; " +
                    "keeping the remaining budget open for the HELLO the reset is still bringing back.");
            }
        }

        Log($"The woken bootrom did not announce HELLO within {StaleRecoveryBudgetMs} ms.");
        return null;
    }

    /// <summary>
    /// Resets the target state machine and returns the HELLO it re-announces, or <see langword="null"/> when the
    /// target stays silent or reports a HELLO this host cannot serve.
    /// <para>复位目标状态机并返回它重新宣告的 HELLO；目标保持静默或报告本主机无法服务的 HELLO 时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <remarks>Only an image-transfer HELLO ends the recovery, because that is the only state the upload loop can
    /// consume. The reset is written before the first read, so leftovers a failed attempt queued are discarded by
    /// the same loop that waits for the re-announcement instead of needing a separate drain.
    /// <para>只有镜像传输状态的 HELLO 会结束恢复，因为那是上传循环唯一可消费的状态。复位在第一次读取之前写入，
    /// 因此失败尝试排队的遗留数据包由同一个等待重宣告的循环丢弃，不需要单独的排空步骤。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The HELLO announcing image transfer, or <see langword="null"/>.
    /// <para>宣告镜像传输的 HELLO；或 <see langword="null"/>。</para></returns>
    private async Task<SaharaHelloPacket?> TryResetTargetAndAwaitHelloAsync(CancellationToken cancellationToken)
    {
        await TryResetStateMachineAsync(cancellationToken).ConfigureAwait(false);
        var deadline = Environment.TickCount64 + StaleRecoveryBudgetMs;
        while (Environment.TickCount64 < deadline)
        {
            var remainingMs = (int)Math.Max(1, deadline - Environment.TickCount64);
            (SaharaPacketHeader Header, byte[] Body) received;
            try
            {
                received = await ReceiveSaharaPacketAsync(
                    Math.Min(remainingMs, StaleRecoveryReadTimeoutMs), cancellationToken).ConfigureAwait(false);
            }
            catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.ReadPortTimeout or EdlErrorCode.TransportTimeout)
            {
                Log($"No packet within {Math.Min(remainingMs, StaleRecoveryReadTimeoutMs)} ms of the recovery budget; " +
                    "keeping the remaining budget open for the HELLO the reset is still bringing back.");
                continue;
            }
            catch (SaharaException ex)
            {
                Log($"Sahara recovery stopped on a packet the host cannot frame: {ex.Message}");
                return null;
            }

            if (TryParseRecoveredHello(received.Header, received.Body, out var hello))
            {
                Log($"The reset target re-announced HELLO (version {hello.Version}, mode {hello.Mode}).");
                return hello;
            }

            Log($"Discarded a Sahara {received.Header.Command} packet while waiting for the reset target to announce HELLO.");
        }

        Log($"The reset target did not announce HELLO within {StaleRecoveryBudgetMs} ms.");
        return null;
    }

    /// <summary>
    /// Parses a HELLO received during recovery and reports whether this host can upload to it.
    /// <para>解析恢复期间收到的 HELLO，并报告本主机是否可以向它上传。</para>
    /// </summary>
    /// <param name="header">The received packet header.<para>收到的数据包头。</para></param>
    /// <param name="body">The received packet body, header excluded.<para>收到的数据包体，不含包头。</para></param>
    /// <param name="hello">The parsed HELLO when this method returns <see langword="true"/>.
    /// <para>本方法返回 <see langword="true"/> 时解析出的 HELLO。</para></param>
    /// <returns><see langword="true"/> for a valid image-transfer HELLO.
    /// <para>合法的镜像传输 HELLO 为 <see langword="true"/>。</para></returns>
    private static bool TryParseRecoveredHello(SaharaPacketHeader header, byte[] body, out SaharaHelloPacket hello)
    {
        hello = default;
        var packetBytes = Marshal.SizeOf<SaharaHelloPacket>();
        if (header.Command != SaharaCommand.Hello
            || header.Length != (uint)packetBytes
            || body.Length != packetBytes - Marshal.SizeOf<SaharaPacketHeader>())
        {
            return false;
        }

        var packet = new byte[packetBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)header.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), header.Length);
        body.CopyTo(packet.AsSpan(Marshal.SizeOf<SaharaPacketHeader>()));
        hello = BytesToStruct<SaharaHelloPacket>(packet);
        return hello.Mode == SaharaMode.ImageTxPending
            && hello.Version >= EdlConstants.SaharaMinVersion
            && hello.MinVersion <= hello.Version
            && hello.MinVersion <= EdlConstants.SaharaHostMaxProtocolVersion;
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

                // A binary Sahara packet is proof the target is a bootrom, not a loader running Firehose: stop here
                // instead of reading out the whole probe budget for a document that cannot arrive, so the caller can
                // move on to the wake that actually brings the announcement back.
                // 二进制 Sahara 数据包即证明目标是 bootrom 而不是运行 Firehose 的 loader：就此停止，不必为一份不可能
                // 到达的文档耗尽整个探测预算，让调用方转去执行真正能唤回宣告的复位。
                if (length == 0 && EdlProbeTransport.Classify(chunk) is not EdlProtocolState.Unknown)
                {
                    Log("The Firehose probe was answered with a Sahara packet; the target is not running Firehose, " +
                        "so the probe stops waiting for a document.");
                    return false;
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
/// <summary>
/// The first packet of an upload: either the HELLO the target announced or the packet that continues a
/// transfer the target already started. The target cannot do both, so the two travel as one value instead of
/// a nullable pair whose members must be kept mutually exclusive by hand.
/// <para>上传的首个数据包：目标宣告的 HELLO，或目标早已开始的传输所继续的数据包。目标不可能同时做这两件事，
/// 因此二者作为一个值传递，而不是一对必须手工维持互斥的可空成员。</para>
/// </summary>
/// <remarks>
/// The cases are positional: the first is the announced HELLO, the second is the complete packet.
/// <para>各分支按位置排列：第一个是宣告的 HELLO，第二个是完整的数据包。</para>
/// </remarks>
internal union SaharaFirstPacket(SaharaHelloPacket, byte[]);