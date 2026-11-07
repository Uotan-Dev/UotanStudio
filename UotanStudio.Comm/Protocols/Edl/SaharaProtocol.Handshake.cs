using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Sahara handshake entry points: HELLO exchange, version negotiation, READ_DATA upload loop and DONE/DONE_RESPONSE completion.
/// <para>Sahara 握手入口：HELLO 交换、版本协商、READ_DATA 上传循环以及 DONE/DONE_RESPONSE 完成。</para>
/// </summary>
public sealed partial class SaharaProtocol
{
    /// <summary>
    /// Number of bounded RESET_STATE_MACHINE attempts made while re-establishing a HELLO that the QUD serial
    /// driver consumed before this host read it.
    /// <para>为重新建立被 QUD 串口驱动在本主机读取之前消费掉的 HELLO，所做有界 RESET_STATE_MACHINE 尝试次数。</para>
    /// </summary>
    private const int QudHelloRecoveryAttempts = 2;

    /// <summary>
    /// Largest payload this host is willing to drain from an unexpected packet so it can resynchronize the
    /// Sahara stream before trying the wake command again.
    /// <para>主机愿意从意外数据包中读取的最大负载字节数，用于在再次尝试唤起命令前重新同步 Sahara 流。</para>
    /// </summary>
    private const int QudHelloRecoveryDrainLimitBytes = 4096;

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
                    "Sending RESET_STATE_MACHINE so the target re-announces HELLO...");
                var recoveredHello = await RecoverQudHelloAsync(cancellationToken).ConfigureAwait(false);
                if (recoveredHello is not null)
                {
                    firstData = recoveredHello;
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
                    Log("Sahara HELLO recovery failed; reporting the original read timeout.");
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

    /// <summary>
    /// Wakes a target whose bootrom HELLO was already consumed by the QUD serial driver by sending
    /// RESET_STATE_MACHINE and then reading the HELLO it re-announces.
    /// <para>通过发送 RESET_STATE_MACHINE 再读取目标重新宣告的 HELLO，唤起 bootrom HELLO 已被 QUD 串口驱动消费的目标。</para>
    /// </summary>
    /// <remarks>
    /// Every attempt is bounded: a target that stays silent simply ends that attempt, and an unexpected
    /// packet is drained to its full protocol length before the next wake, so a following read cannot
    /// start in the middle of a half-consumed packet. <see langword="null"/> means no HELLO arrived; the
    /// caller keeps the original read-timeout semantics.
    /// <para>每次尝试都有界：保持静默的目标只会结束当前尝试，意外数据包则在下次唤起之前按完整协议长度读掉，
    /// 因此后续读取不会从半个数据包的中间开始。<see langword="null"/> 表示没有收到 HELLO；调用方保持原始读取
    /// 超时语义。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token checked before every wake and read.
    /// <para>在每次唤起与读取之前检查的取消令牌。</para></param>
    /// <returns>The 48-byte HELLO packet, or <see langword="null"/> when no bounded attempt produced one.
    /// <para>48 字节的 HELLO 包；没有任何有界尝试得到时为 <see langword="null"/>。</para></returns>
    private async Task<byte[]?> RecoverQudHelloAsync(CancellationToken cancellationToken)
    {
        var helloBytes = Marshal.SizeOf<SaharaHelloPacket>();
        var headerBytes = Marshal.SizeOf<SaharaPacketHeader>();
        var wakeTimeoutMs = Math.Min(_timeoutMs, EdlConstants.HelloProbeTimeoutMs);
        for (var attempt = 1; attempt <= QudHelloRecoveryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reset = BuildSaharaSimpleCommand(SaharaCommand.ResetState);
                await WriteDataAsync(reset, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"RESET_STATE_MACHINE attempt {attempt} could not be written: {ex.Message}");
                CommTrace.Debug("edl", $"Sahara HELLO recovery: RESET_STATE_MACHINE write failed ({ex.Message}).");
                return null;
            }

            Log($"Sent RESET_STATE_MACHINE (attempt {attempt} of {QudHelloRecoveryAttempts}) so the target re-announces HELLO.");
            byte[] headerData;
            try
            {
                headerData = await ReadExactAsync(headerBytes, wakeTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
            {
                Log($"No Sahara packet arrived after RESET_STATE_MACHINE attempt {attempt}.");
                CommTrace.Debug("edl", $"Sahara HELLO recovery: no packet after wake attempt {attempt}.");
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Reading the packet after RESET_STATE_MACHINE attempt {attempt} failed: {ex.Message}");
                CommTrace.Debug("edl",
                    $"Sahara HELLO recovery: packet read failed on wake attempt {attempt} ({ex.Message}).");
                return null;
            }

            var header = BytesToStruct<SaharaPacketHeader>(headerData);
            if (header.Command == SaharaCommand.Hello && header.Length == (uint)helloBytes)
            {
                byte[] body;
                try
                {
                    body = await ReadExactAsync(helloBytes - headerBytes, wakeTimeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"The HELLO after RESET_STATE_MACHINE attempt {attempt} was truncated: {ex.Message}");
                    CommTrace.Debug("edl", $"Sahara HELLO recovery: truncated HELLO on wake attempt {attempt}.");
                    return null;
                }
                var hello = new byte[helloBytes];
                headerData.CopyTo(hello, 0);
                body.CopyTo(hello, headerBytes);
                Log("The target re-announced HELLO after RESET_STATE_MACHINE.");
                CommTrace.Debug("edl",
                    $"Sahara HELLO recovery: HELLO received after wake attempt {attempt}.");
                return hello;
            }

            Log($"Received {header.Command} (len {header.Length}) instead of HELLO after RESET_STATE_MACHINE.");
            if (!await TryDrainUnexpectedSaharaPacketAsync(header, cancellationToken).ConfigureAwait(false))
            {
                CommTrace.Debug("edl",
                    $"Sahara HELLO recovery: could not drain {header.Command} (len {header.Length}); attempt stopped.");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Consumes the remaining bytes of an unexpected Sahara packet so the next read starts on a packet boundary.
    /// <para>读取意外 Sahara 数据包的全部剩余字节，使下一次读取从数据包边界开始。</para>
    /// </summary>
    /// <param name="header">The already-read packet header. <para>已读取的数据包头。</para></param>
    /// <param name="cancellationToken">Cancellation token for the bounded drain read.
    /// <para>用于有界排出读取的取消令牌。</para></param>
    /// <returns><see langword="true"/> when the whole packet was drained, <see langword="false"/> when the
    /// reported length is unusable or the drain could not finish.
    /// <para>整包已读掉时为 <see langword="true"/>；上报长度不可用或未能读完时为
    /// <see langword="false"/>。</para></returns>
    private async Task<bool> TryDrainUnexpectedSaharaPacketAsync(SaharaPacketHeader header,
        CancellationToken cancellationToken)
    {
        var headerBytes = (uint)Marshal.SizeOf<SaharaPacketHeader>();
        if (header.Length < headerBytes || header.Length - headerBytes > QudHelloRecoveryDrainLimitBytes)
        {
            return false;
        }

        var remaining = (int)(header.Length - headerBytes);
        if (remaining == 0)
        {
            return true;
        }

        try
        {
            await ReadExactAsync(remaining, _timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Draining {remaining} bytes of {header.Command} failed: {ex.Message}");
            return false;
        }
        CommTrace.Debug("edl", $"Sahara HELLO recovery: drained {remaining} bytes of {header.Command}.");
        return true;
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
        0x0A => "invalid data offset",
        0x0B => "invalid data length",
        0x0C => "invalid image id",
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
            var nopBytes = Encoding.UTF8.GetBytes(EdlConstants.XmlHeader + EdlConstants.DataOpen + "<nop value=\"ping\"/>\n" + EdlConstants.DataClose);
            await WriteDataAsync(nopBytes, cancellationToken);

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

    /// <summary>
    /// Checks if the data looks like a Firehose XML response after skipping the NUL packet padding and ASCII
    /// whitespace a target emits before its document.
    /// <para>在跳过目标在文档之前输出的 NUL 包填充与 ASCII 空白后，检查数据是否看起来像 Firehose XML 响应。</para>
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
}