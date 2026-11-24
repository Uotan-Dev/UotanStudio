using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// Public UNISOC download-session facade: identifies the loader stage on an opened download link (Boot ROM, FDL1
/// or an already-running FDL2), uploads the two loaders with the documented per-stage checksums, and exposes the
/// FDL2 storage face, chip UID and completion modes. The link is the vendor download serial port
/// (<c>1782:4D00</c>) or an equivalent opened transport.
/// <para>紫光展锐下载会话的公共门面：在已打开的下载链路上识别加载阶段（Boot ROM、FDL1 或已运行的 FDL2）、
/// 按文档的分阶段校验上传两级加载器，并暴露 FDL2 存储面、芯片 UID 与收尾模式。链路是厂商下载串口
/// （<c>1782:4D00</c>）或等价的已打开传输。</para>
/// </summary>
/// <remarks>
/// Stage detection follows the hardened probe line: a single-byte CHECK_BAUD probe is parsed under CRC-16
/// (Boot ROM, including the empty <c>VERIFY_ERROR</c> correction line) and then under the additive checksum (FDL1
/// version, or the empty <c>UNSUPPORTED_COMMAND</c> of a running FDL2); a silent link allows one CONNECT query and
/// one retried CHECK_BAUD handshake within the same budget. Any other reply, a malformed frame or budget
/// exhaustion faults the session instead of guessing. Devices whose Boot ROM does not report <c>SPRD3</c> require
/// correctly signed loaders; this class never bypasses signature checks.
/// <para>阶段识别遵循强化的探测线：单字节 CHECK_BAUD 先按 CRC-16 解析（Boot ROM，含空 <c>VERIFY_ERROR</c> 修正线），
/// 再按加性校验解析（FDL1 的版本帧，或已运行 FDL2 的空 <c>UNSUPPORTED_COMMAND</c>）；静默链路允许在同预算内做一次
/// CONNECT 查询与一次带重试的 CHECK_BAUD 握手。其他应答、坏帧或预算耗尽都会使会话失效而不是继续猜测。Boot ROM
/// 应答不是 <c>SPRD3</c> 的设备要求正确签名的加载器；本类不做任何签名绕过。</para>
/// </remarks>
internal sealed class UnisocDeviceEngine : UnisocSession
{
    private UnisocDeviceEngine(IUnisocTransport transport, UnisocSessionOptions options)
        : base(transport, options)
    {
    }

    /// <summary>Gets the identity captured during the handshake.
    /// <para>获取握手期间捕获的身份信息。</para></summary>
    public UnisocDeviceIdentity Identity { get; private set; } =
        new(UnisocBootStage.None, null, SecureBoot: false);

    /// <summary>Gets the identified loader stage. <para>获取识别出的加载阶段。</para></summary>
    public UnisocBootStage Stage => Identity.Stage;

    /// <summary>Gets the validated ordinary-command timeout. <para>获取已校验的普通命令超时。</para></summary>
    internal int CommandTimeoutMs => Options.CommandTimeoutMs;

    /// <summary>Gets the validated storage-chunk timeout. <para>获取已校验的存储分块超时。</para></summary>
    internal int StorageChunkTimeoutMs => Options.StorageChunkTimeoutMs;

    /// <summary>Gets the validated erase timeout. <para>获取已校验的擦除超时。</para></summary>
    internal int EraseTimeoutMs => Options.EraseTimeoutMs;

    /// <summary>Gets the validated storage block size. <para>获取已校验的存储分块大小。</para></summary>
    internal int StorageBlockSize => Options.StorageBlockSize;

    /// <summary>Faults the session from an internal storage-face error path.
    /// <para>从内部存储面的错误路径使会话失效。</para></summary>
    internal new void Fault() => base.Fault();

    /// <summary>
    /// Opens the session: identifies the loader stage on the link. On success the session is connected to the
    /// identified stage; on failure the transport has been closed.
    /// <para>打开会话：识别链路上的加载阶段。成功时会话已连接到识别出的阶段；失败时传输已被关闭。</para>
    /// </summary>
    /// <param name="transport">The opened transport; the session owns and disposes it.
    /// <para>已打开的传输；会话持有并释放它。</para></param>
    /// <param name="options">Session parameters, or <see langword="null"/> for the defaults.
    /// <para>会话参数；<see langword="null"/> 表示使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消握手。</para></param>
    /// <returns>The connected session. <para>已连接的会话。</para></returns>
    /// <exception cref="UnisocProtocolException">The link did not answer the probe lines with a recognizable stage.
    /// <para>链路对探测线没有给出可识别的阶段应答。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static async Task<UnisocDeviceEngine> OpenAsync(IUnisocTransport transport,
        UnisocSessionOptions? options, CancellationToken cancellationToken)
    {
        var session = new UnisocDeviceEngine(transport, options ?? new UnisocSessionOptions());
        try
        {
            await transport.SendDownloadModeControlAsync(cancellationToken).ConfigureAwait(false);
            var identity = await session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
            session.Identity = identity;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task<UnisocDeviceIdentity> IdentifyAsync(CancellationToken cancellationToken)
    {
        // The official tool repeats CHECK_BAUD on a silent/failed exchange instead of concluding after one probe.
        // Re-send the bare header each attempt and re-parse with both algorithms, because a device that has just
        // re-enumerated may only answer on the second or third line.
        UnisocFrame? crcReply = null;
        var crcMalformed = false;
        for (var attempt = 0; attempt < Options.CheckBaudRetries && crcReply is null && !crcMalformed; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The write budget must be the full command timeout, not the short reply budget: a CDC/VCOM port
            // that has just enumerated can take longer than the reply window to accept the first byte, and a
            // write timeout here faults the session and prevents any silent retry.
            await Transport.WriteAsync(UnisocFrames.EncodeCheckBaud(1), cancellationToken, Options.CommandTimeoutMs)
                .ConfigureAwait(false);
            (crcReply, crcMalformed) = await CollectReplyAsync(UnisocChecksumKind.Crc16,
                Options.EntryProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (crcReply is { } crcFrame)
        {
            if ((UnisocReply)crcFrame.Type == UnisocReply.Version)
            {
                return await BootRomAsync(crcFrame.Payload, cancellationToken).ConfigureAwait(false);
            }

            if ((UnisocReply)crcFrame.Type == UnisocReply.VerifyError && crcFrame.PayloadLength == 0)
            {
                await ConnectAsync(UnisocChecksumKind.Crc16, cancellationToken).ConfigureAwait(false);
                DiscardBuffered();
                return new UnisocDeviceIdentity(UnisocBootStage.BootRom, null, SecureBoot: true);
            }

            Fault();
            throw new UnisocProtocolException(
                $"The Unisoc link replied 0x{crcFrame.Type:X4} to the stage probe, which identifies no stage.");
        }

        (UnisocFrame? sumReply, bool sumMalformed) = await CollectReplyAsync(UnisocChecksumKind.Sum,
            Options.EntryProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (sumReply is { } sumFrame)
        {
            if ((UnisocReply)sumFrame.Type == UnisocReply.Version)
            {
                await ConnectAsync(UnisocChecksumKind.Sum, cancellationToken).ConfigureAwait(false);
                DiscardBuffered();
                return new UnisocDeviceIdentity(UnisocBootStage.Fdl1, sumFrame.Payload, SecureBoot: false);
            }

            if ((UnisocReply)sumFrame.Type == UnisocReply.UnsupportedCommand && sumFrame.PayloadLength == 0)
            {
                await ConnectAsync(UnisocChecksumKind.Sum, cancellationToken).ConfigureAwait(false);
                DiscardBuffered();
                return new UnisocDeviceIdentity(UnisocBootStage.Fdl2, null, SecureBoot: false);
            }

            Fault();
            throw new UnisocProtocolException(
                $"The Unisoc link replied 0x{sumFrame.Type:X4} to the stage probe, which identifies no stage.");
        }

        if (crcMalformed && sumMalformed)
        {
            Fault();
            throw new UnisocProtocolException(
                "The Unisoc link delivered probe replies that verify under neither checksum algorithm.");
        }

        var silent = await TrySilentHandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (silent is not null)
        {
            return silent;
        }

        Fault();
        throw new UnisocProtocolException("The Unisoc download link did not identify a loader stage.");
    }

    private async Task<UnisocDeviceIdentity> BootRomAsync(byte[] version, CancellationToken cancellationToken)
    {
        await ConnectAsync(UnisocChecksumKind.Crc16, cancellationToken).ConfigureAwait(false);
        DiscardBuffered();
        var open = version.Length == UnisocConstants.OpenBootRomVersion.Length + 1 &&
            version.AsSpan(0, UnisocConstants.OpenBootRomVersion.Length)
                .SequenceEqual(UnisocConstants.OpenBootRomVersion);
        return new UnisocDeviceIdentity(UnisocBootStage.BootRom, version, SecureBoot: !open);
    }

    private async Task<UnisocDeviceIdentity?> TrySilentHandshakeAsync(CancellationToken cancellationToken)
    {
        // Same write/reply budget split as the CHECK_BAUD probe: give the port the full command timeout to
        // accept the CONNECT bytes while keeping the short reply window for the silent-link handshake.
        await SendCommandAsync(UnisocCommand.Connect, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        (UnisocFrame? ackFrame, bool _) = await CollectReplyAsync(UnisocChecksumKind.Sum, Options.EntryProbeTimeoutMs,
            cancellationToken).ConfigureAwait(false);
        if (ackFrame is not { } ack || (UnisocReply)ack.Type != UnisocReply.Ack)
        {
            return null;
        }

        SetChecksumKind(UnisocChecksumKind.Sum);
        DiscardBuffered();
        for (var attempt = 0; attempt < Options.CheckBaudRetries; attempt++)
        {
            var probeReply = await ProbeAsync(UnisocFrames.EncodeCheckBaud(1), UnisocChecksumKind.Sum,
                Options.EntryProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (probeReply is not { } reply)
            {
                continue;
            }

            if ((UnisocReply)reply.Type == UnisocReply.Version)
            {
                await ConnectAsync(UnisocChecksumKind.Sum, cancellationToken).ConfigureAwait(false);
                DiscardBuffered();
                return new UnisocDeviceIdentity(UnisocBootStage.Fdl1, reply.Payload, SecureBoot: false);
            }

            if ((UnisocReply)reply.Type == UnisocReply.UnsupportedCommand && reply.PayloadLength == 0)
            {
                await ConnectAsync(UnisocChecksumKind.Sum, cancellationToken).ConfigureAwait(false);
                DiscardBuffered();
                return new UnisocDeviceIdentity(UnisocBootStage.Fdl2, null, SecureBoot: false);
            }

            return null;
        }

        return null;
    }

    private async Task ConnectAsync(UnisocChecksumKind kind, CancellationToken cancellationToken)
    {
        SetChecksumKind(kind);
        await SendCommandAsync(UnisocCommand.Connect, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        (UnisocFrame? ackFrame, bool _) = await CollectReplyAsync(kind, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (ackFrame is not { } ack || (UnisocReply)ack.Type != UnisocReply.Ack)
        {
            Fault();
            throw new UnisocProtocolException("The Unisoc link refused the CONNECT acknowledgement.");
        }

        DiscardBuffered();
    }

    /// <summary>
    /// Uploads and executes the first-stage loader from a Boot ROM connection. On success the link talks to FDL1
    /// under the additive checksum.
    /// <para>从 Boot ROM 连接上传并执行一级加载器。成功后链路以加性校验与 FDL1 通信。</para>
    /// </summary>
    /// <param name="loader">Readable loader image; the session transfers the bytes from its current position and
    /// never materializes it.
    /// <para>可读的加载器镜像；会话从当前位置开始逐块传输，不做物化。</para></param>
    /// <param name="address">RAM load address confirmed by the package scheme or the user.
    /// <para>由包 scheme 或用户确认的 RAM 加载地址。</para></param>
    /// <param name="cancellationToken">Cancels the upload. <para>取消上传。</para></param>
    /// <exception cref="InvalidOperationException">The session is not connected to the Boot ROM.
    /// <para>会话未连接到 Boot ROM。</para></exception>
    /// <exception cref="UnisocProtocolException">Any exchange failed; the session has faulted.
    /// <para>任一交互失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task UploadFdl1Async(Stream loader, uint address, CancellationToken cancellationToken)
    {
        if (Stage != UnisocBootStage.BootRom)
        {
            throw new InvalidOperationException("The first-stage loader requires a Boot ROM connection.");
        }

        await UploadLoaderAsync(loader, address, UnisocChecksumKind.Crc16, cancellationToken).ConfigureAwait(false);
        var exec = await ExchangeAsync(UnisocCommand.ExecData, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if ((UnisocReply)exec.Type != UnisocReply.Ack)
        {
            Fault();
            throw new UnisocProtocolException($"The device replied 0x{exec.Type:X4} to the first-stage exec.");
        }

        if (Options.Fdl1StartDelayMs > 0)
        {
            await Task.Delay(Options.Fdl1StartDelayMs, cancellationToken).ConfigureAwait(false);
        }

        SetChecksumKind(UnisocChecksumKind.Sum);
        UnisocFrame? version = null;
        for (var attempt = 0; attempt < Options.CheckBaudRetries && version is null; attempt++)
        {
            version = await ProbeAsync(UnisocFrames.EncodeCheckBaud(1), UnisocChecksumKind.Sum,
                Options.EntryProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
        }

        if (version is null || (UnisocReply)version.Value.Type != UnisocReply.Version)
        {
            Fault();
            throw new UnisocProtocolException("The first-stage loader did not report its version.");
        }

        await ConnectAsync(UnisocChecksumKind.Sum, cancellationToken).ConfigureAwait(false);
        Identity = Identity with { Stage = UnisocBootStage.Fdl1, Version = version.Value.Payload };
    }

    /// <summary>
    /// Uploads and executes the second-stage loader from an FDL1 connection. On success the link talks to FDL2.
    /// <para>从 FDL1 连接上传并执行二级加载器。成功后链路与 FDL2 通信。</para>
    /// </summary>
    /// <param name="loader">Readable loader image. <para>可读的加载器镜像。</para></param>
    /// <param name="address">RAM load address. <para>RAM 加载地址。</para></param>
    /// <param name="cancellationToken">Cancels the upload. <para>取消上传。</para></param>
    /// <exception cref="InvalidOperationException">The session is not connected to FDL1.
    /// <para>会话未连接到 FDL1。</para></exception>
    /// <exception cref="UnisocProtocolException">Any exchange failed; the session has faulted.
    /// <para>任一交互失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task UploadFdl2Async(Stream loader, uint address, CancellationToken cancellationToken)
    {
        if (Stage != UnisocBootStage.Fdl1)
        {
            throw new InvalidOperationException("The second-stage loader requires an FDL1 connection.");
        }

        await UploadLoaderAsync(loader, address, UnisocChecksumKind.Sum, cancellationToken).ConfigureAwait(false);
        var exec = await ExchangeAsync(UnisocCommand.ExecData, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if ((UnisocReply)exec.Type is not (UnisocReply.Ack or UnisocReply.IncompatiblePartition))
        {
            Fault();
            throw new UnisocProtocolException($"The device replied 0x{exec.Type:X4} to the second-stage exec.");
        }

        Identity = Identity with { Stage = UnisocBootStage.Fdl2 };
    }

    private async Task UploadLoaderAsync(Stream loader, uint address, UnisocChecksumKind kind,
        CancellationToken cancellationToken)
    {
        if (!loader.CanSeek)
        {
            throw new ArgumentException(
                "The loader stream must be seekable so the declared size is known.", nameof(loader));
        }

        var size = loader.Length - loader.Position;
        if (size is < 1 or > 32 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(loader), size,
                "A loader image must have between one byte and 32 MiB remaining from its current position.");
        }

        if ((ulong)address + (ulong)size > 0x1_0000_0000UL)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address,
                "The loader image would extend past the 32-bit RAM boundary.");
        }

        SetChecksumKind(kind);

        var start = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(start, address);
        BinaryPrimitives.WriteUInt32BigEndian(start.AsSpan(4), (uint)size);
        await SendAndExpectAckAsync(UnisocCommand.StartData, start, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[Options.LoaderBlockSize];
        long transferred = 0;
        while (transferred < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(buffer.Length, size - transferred);
            await loader.ReadExactlyAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            await SendAndExpectAckAsync(UnisocCommand.MidstData, buffer.AsMemory(0, chunk),
                Options.CommandTimeoutMs, cancellationToken).ConfigureAwait(false);
            transferred += chunk;
        }

        await SendAndExpectAckAsync(UnisocCommand.EndData, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Disables the HDLC escape transcoding after the device acknowledges it; every later frame is sent unescaped.
    /// <para>在设备确认后关闭 HDLC 转义；此后每条帧都按未转义发送。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused or the link failed; the session has faulted.
    /// <para>设备拒绝或链路失败；会话已失效。</para></exception>
    /// <exception cref="InvalidOperationException">The transcode is already disabled.
    /// <para>转义已关闭。</para></exception>
    public async Task DisableTranscodeAsync(CancellationToken cancellationToken)
    {
        await SendAndExpectAckAsync(UnisocCommand.DisableTranscode, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        SetTranscodeDisabled();
    }

    /// <summary>
    /// Reads the chip UID. <para>读取芯片 UID。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <returns>The raw UID bytes, 1 to 256. <para>原始 UID 字节，1 到 256 字节。</para></returns>
    /// <exception cref="UnisocProtocolException">The device refused or the payload was out of bounds; the session
    /// has faulted. <para>设备拒绝或负载越界；会话已失效。</para></exception>
    public async Task<byte[]> ReadChipUidAsync(CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(UnisocCommand.ReadChipUid, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if ((UnisocReply)reply.Type != UnisocReply.ReadChipUid || reply.PayloadLength is < 1 or > 256)
        {
            Fault();
            throw new UnisocProtocolException(
                $"The device replied 0x{reply.Type:X4} with {reply.PayloadLength} UID bytes.");
        }

        return reply.Payload;
    }

    /// <summary>Resets the device into normal mode. <para>把设备重启进正常模式。</para></summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused; the session has faulted.
    /// <para>设备拒绝；会话已失效。</para></exception>
    public async Task NormalResetAsync(CancellationToken cancellationToken)
    {
        await SendAndExpectAckAsync(UnisocCommand.NormalReset, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Powers the device off. <para>让设备关机。</para></summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused; the session has faulted.
    /// <para>设备拒绝；会话已失效。</para></exception>
    public async Task PowerOffAsync(CancellationToken cancellationToken)
    {
        await SendAndExpectAckAsync(UnisocCommand.PowerOff, ReadOnlyMemory<byte>.Empty, Options.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the FDL2 storage face; every operation re-checks the stage before it touches the device.
    /// <para>打开 FDL2 存储面；每个操作在触碰设备前都会重新检查阶段。</para>
    /// </summary>
    /// <returns>The storage face bound to this session. <para>绑定本会话的存储面。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not connected to FDL2.
    /// <para>会话未连接到 FDL2。</para></exception>
    public UnisocStorageSession OpenStorage()
    {
        if (Stage != UnisocBootStage.Fdl2)
        {
            throw new InvalidOperationException("Storage operations require an FDL2 connection.");
        }

        return new UnisocStorageSession(this);
    }
}