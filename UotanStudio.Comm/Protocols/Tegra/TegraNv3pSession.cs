using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// State the nv3p service reports for the work it just finished, decoded from the <c>NV3P_CMD_STATUS</c> packet the
/// device sends on its own.
/// <para>nv3p 服务对刚完成工作上报的状态，解码自设备主动发送的 <c>NV3P_CMD_STATUS</c> 包。</para>
/// </summary>
/// <param name="Message">The 32-byte, null-terminated status text the device returned, with the padding removed.
/// <para>设备返回的 32 字节、以零结尾的状态文本，已去掉填充。</para></param>
/// <param name="Code">The status code, one of the <c>nv3p_status_*</c> values; unknown values are preserved.
/// <para>状态码，即 <c>nv3p_status_*</c> 之一；未知取值原样保留。</para></param>
/// <param name="Flags">The device flag word, which the protocol reserves but still carries.
/// <para>设备标志字，协议保留该字段但仍会携带。</para></param>
public sealed record TegraNv3pDeviceStatus(string Message, uint Code, uint Flags)
{
    /// <summary>
    /// Gets whether the reported status code is <c>nv3p_status_ok</c> (zero).
    /// <para>获取上报状态码是否为 <c>nv3p_status_ok</c>（零）。</para>
    /// </summary>
    /// <value><see langword="true"/> only for the success code. <para>仅成功码为 <see langword="true"/>。</para></value>
    public bool IsOk => Code == 0;
}

/// <summary>
/// nv3p session the host runs once the miniloader is executing: it sends command packets, transfers payloads with
/// data packets and answers the status packets the device initiates.
/// <para>miniloader 开始运行后主机使用的 nv3p 会话：发送命令包、以数据包传输负载，并应答设备主动发起的状
/// 态包。</para>
/// </summary>
/// <remarks>
/// <para>The framing follows <c>tegrarcm</c>'s <c>src/nv3p.c</c>. Every packet starts with the 12-byte basic header —
/// packet type, sequence and checksum — so <see cref="TegraNv3pConstants.BasicPacketSize"/> is the header alone and
/// a command adds <see cref="TegraNv3pConstants.CommandPacketSize"/> bytes, a data packet
/// <see cref="TegraNv3pConstants.DataPacketSize"/> bytes and a negative acknowledgement
/// <see cref="TegraNv3pConstants.NackPacketSize"/> bytes. A received packet is valid only when its checksum plus the
/// local byte sum of everything received for that packet is zero; a mismatch raises
/// <see cref="TegraProtocolException"/> before any of the packet is interpreted. Sequence numbers increase with every
/// packet this session sends, and the device echoes the last one it accepted, so a reply carrying any other value is
/// treated as a protocol violation.</para>
/// <para>分帧遵循 <c>tegrarcm</c> 的 <c>src/nv3p.c</c>。每个包都以 12 字节基础包头开始——包类型、序号与校验和——
/// 因此 <see cref="TegraNv3pConstants.BasicPacketSize"/> 只是包头，命令包再加
/// <see cref="TegraNv3pConstants.CommandPacketSize"/> 字节，数据包加
/// <see cref="TegraNv3pConstants.DataPacketSize"/> 字节，否定应答加
/// <see cref="TegraNv3pConstants.NackPacketSize"/> 字节。只有当收到的校验和与该包全部收到字节的本地求和相加为零时，
/// 包才有效；不匹配时在解释任何字段之前抛出 <see cref="TegraProtocolException"/>。序号随本会话发出的每个包递增，
/// 设备回显其最后接受的序号，因此携带其它取值的应答按协议违规处理。</para>
/// <para>Device replies are untrusted input: every declared length is bounded before it drives a read or an
/// allocation, and the session never trusts the device to keep a payload inside
/// <see cref="TegraNv3pConstants.MaximumDataPayloadLength"/>.</para>
/// <para>设备应答按不可信输入处理：每个声明的长度在驱动读取或分配之前都有上界，会话不信任设备会把负载限制在
/// <see cref="TegraNv3pConstants.MaximumDataPayloadLength"/> 之内。</para>
/// </remarks>
public sealed class TegraNv3pSession : IDisposable
{
    /// <summary>
    /// Default timeout of a single transport transfer, in milliseconds.
    /// <para>单次传输的默认超时（毫秒）。</para>
    /// </summary>
    public const int DefaultTimeoutMilliseconds = 10_000;

    /// <summary>
    /// Largest payload this session writes in one data packet (64 KiB), matching the chunk the reference flasher
    /// uses. <para>本会话在单个数据包中写出的最大负载（64 KiB），与参考烧录器的分块一致。</para>
    /// </summary>
    public const int MaximumDownloadChunkLength = 64 * 1024;

    /// <summary>
    /// Largest command-argument block this session accepts (1 KiB); the widest command, <c>DL_BL</c>, needs 16
    /// bytes. <para>本会话接受的命令参数块上限（1 KiB）；最宽的命令 <c>DL_BL</c> 只需要 16 字节。</para>
    /// </summary>
    public const int MaximumCommandArgumentLength = 1024;

    /// <summary>
    /// Wire size of <c>nv3p_platform_info_t</c>: two unique-id words, the chip id, the SKU, the version, the boot
    /// device, the operating mode, three configuration words, two reserved words and the four-word board id.
    /// <para><c>nv3p_platform_info_t</c> 的线上长度：两个唯一 ID 字、芯片 ID、SKU、版本、引导设备、操作模式、
    /// 三个配置字、两个保留字以及四个字的板卡 ID。</para>
    /// </summary>
    public const int PlatformInfoLength = 72;

    /// <summary>
    /// Wire size of <c>nv3p_cmd_status_t</c>: the 32-byte text, the code word and the flags word.
    /// <para><c>nv3p_cmd_status_t</c> 的线上长度：32 字节文本、代码字与标志字。</para>
    /// </summary>
    public const int StatusLength = TegraNv3pConstants.StatusStringLength + (2 * sizeof(uint));

    /// <summary>
    /// Wire size of <c>nv3p_bct_info_t</c>, the single length word <c>GET_BCT</c> answers with.
    /// <para><c>nv3p_bct_info_t</c> 的线上长度，即 <c>GET_BCT</c> 应答携带的单个长度字。</para>
    /// </summary>
    public const int BctInfoLength = sizeof(uint);

    /// <summary>
    /// Largest BCT this session accepts from a device (1 MiB); a real BCT is a few KiB.
    /// <para>本会话接受的 BCT 上限（1 MiB）；真实 BCT 只有几 KiB。</para>
    /// </summary>
    public const int MaximumBctLength = 1024 * 1024;

    /// <summary>
    /// Largest bootloader image this session sends in one download (64 MiB), which bounds the length word the host
    /// puts on the wire. <para>本会话一次下载发送的 bootloader 镜像上限（64 MiB），用于约束主机写入线上的长度字。</para>
    /// </summary>
    public const int MaximumBootloaderLength = 64 * 1024 * 1024;

    private readonly int _timeoutMs;
    private uint _receivedChecksum;
    private uint _receivedPayloadRemaining;
    private uint _receivedSequence;
    private uint _sequence;
    private bool _disposed;

    /// <summary>
    /// Initializes a session over an open transport; the miniloader must already be running.
    /// <para>在已打开的传输上初始化会话；此时 miniloader 必须已在运行。</para>
    /// </summary>
    /// <param name="transport">The transport the miniloader answers on. <para>miniloader 应答所用的传输。</para></param>
    /// <param name="timeoutMs">Optional timeout of a single transfer in milliseconds; a non-positive value is
    /// rejected. <para>可选的单次传输超时（毫秒）；非正值会被拒绝。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> is <see langword="null"/>.
    /// <para><paramref name="transport"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is zero or negative.
    /// <para><paramref name="timeoutMs"/> 为零或负值。</para></exception>
    public TegraNv3pSession(ICommTransport transport, int? timeoutMs = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs ?? DefaultTimeoutMilliseconds;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_timeoutMs);
    }

    /// <summary>
    /// Gets the protocol name, the constant <c>"nv3p"</c>.
    /// <para>获取协议名称，即常量 <c>"nv3p"</c>。</para>
    /// </summary>
    public string ProtocolName => "nv3p";

    /// <summary>
    /// Gets the underlying transport, which this session never disposes.
    /// <para>获取底层传输；本会话在释放时一并释放它。</para>
    /// </summary>
    public ICommTransport Transport { get; }

    /// <summary>
    /// Gets whether the session is usable: it has not been disposed and the transport still reports a connection.
    /// <para>获取会话是否可用：尚未释放且传输仍报告已连接。</para>
    /// </summary>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Gets the sequence number the next outgoing packet carries; it starts at zero and increases with every packet
    /// the session sends. <para>获取下一个发出的包携带的序号；自零开始，随会话发出的每个包递增。</para>
    /// </summary>
    public uint Sequence => _sequence;

    /// <summary>
    /// Reads the platform information with <c>NV3P_CMD_GET_PLATFORM_INFO</c> and parses it into
    /// <see cref="TegraPlatformInfo"/>, field for field in the order of <c>nv3p_platform_info_t</c>.
    /// <para>用 <c>NV3P_CMD_GET_PLATFORM_INFO</c> 读取平台信息并按 <c>nv3p_platform_info_t</c> 的字段顺序逐字段解析为
    /// <see cref="TegraPlatformInfo"/>。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the transfer. <para>取消本次传输。</para></param>
    /// <returns>The decoded platform information, whose operating mode gates whether a download may follow.
    /// <para>解码出的平台信息，其操作模式决定后续是否允许下载。</para></returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已释放。</para></exception>
    /// <exception cref="TegraProtocolException">The device answered with a bad checksum, an unexpected packet type or
    /// sequence, a non-success status, or a reply too short to hold the whole structure.
    /// <para>设备应答的校验和错误、包类型或序号不符、状态非成功，或应答短于整个结构。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<TegraPlatformInfo> GetPlatformInfoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendCommandAsync(TegraNv3pCommand.GetPlatformInfo, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[PlatformInfoLength];
        await ReceiveDataAsync(buffer, cancellationToken).ConfigureAwait(false);

        // The reference reads the status packet after the platform info before it uses any of the values, so a
        // device that refuses the query never reaches the caller as a half-filled structure.
        // 参考实现在使用任何取值之前先读平台信息之后的状态包，因此拒绝该查询的设备不会以半填充结构到达调用方。
        await WaitForSuccessStatusAsync("get platform info", cancellationToken).ConfigureAwait(false);

        return ParsePlatformInfo(buffer);
    }

    /// <summary>
    /// Reads the BCT that currently sits on the boot device with <c>NV3P_CMD_GET_BCT</c>.
    /// <para>用 <c>NV3P_CMD_GET_BCT</c> 读取引导设备上当前的 BCT。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the transfer. <para>取消本次传输。</para></param>
    /// <returns>The BCT bytes, exactly as many as the device declared. <para>BCT 字节，数量与设备声明的一致。</para></returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已释放。</para></exception>
    /// <exception cref="TegraProtocolException">The checksum, packet type, sequence or status is wrong, or the
    /// declared BCT length is zero, above <see cref="MaximumBctLength"/> or larger than the transfer completes.
    /// <para>校验和、包类型、序号或状态有误，或声明的 BCT 长度为零、超过 <see cref="MaximumBctLength"/>、或传输未能
    /// 完成该长度。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<byte[]> GetBctAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendCommandAsync(TegraNv3pCommand.GetBct, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);

        var info = new byte[BctInfoLength];
        await ReceiveDataAsync(info, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(info);
        if (length == 0 || length > MaximumBctLength)
        {
            throw new TegraProtocolException(
                $"The device declared a {length}-byte BCT, outside the 1..{MaximumBctLength} byte range.");
        }

        await WaitForSuccessStatusAsync("get BCT", cancellationToken).ConfigureAwait(false);

        var bct = new byte[(int)length];
        await ReceiveDataAsync(bct, cancellationToken).ConfigureAwait(false);
        return bct;
    }

    /// <summary>
    /// Downloads a BCT with <c>NV3P_CMD_DL_BCT</c>: the command carries the payload length, the payload follows in
    /// data packets, and the device answers with its status once the last one is acknowledged.
    /// <para>用 <c>NV3P_CMD_DL_BCT</c> 下载 BCT：命令携带负载长度，负载随后以数据包发送，设备在最后一个包被确认后
    /// 以状态应答。</para>
    /// </summary>
    /// <param name="bct">The BCT bytes to send; empty input is rejected before any byte goes on the wire.
    /// <para>待发送的 BCT 字节；空输入在任何字节上线之前即被拒绝。</para></param>
    /// <param name="progress">Optional sink for transfer progress. <para>可选的传输进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels between chunks. <para>在分块之间取消。</para></param>
    /// <returns>A task that completes once the device reports success. <para>设备上报成功后完成的任务。</para></returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已释放。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="bct"/> is empty or above
    /// <see cref="MaximumBctLength"/>. <para><paramref name="bct"/> 为空或超过
    /// <see cref="MaximumBctLength"/>。</para></exception>
    /// <exception cref="TegraProtocolException">The device rejected the command or the transfer failed its checks.
    /// <para>设备拒绝了该命令，或传输未通过校验。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task DownloadBctAsync(ReadOnlyMemory<byte> bct,
        IProgress<TegraNv3pProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bct.IsEmpty || bct.Length > MaximumBctLength)
        {
            throw new ArgumentException(
                $"A BCT download carries 1 to {MaximumBctLength} bytes, but {bct.Length} were supplied.",
                nameof(bct));
        }

        var arguments = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, (uint)bct.Length);
        await SendCommandAsync(TegraNv3pCommand.DownloadBct, arguments, cancellationToken).ConfigureAwait(false);
        await SendPayloadAsync(bct, "bct", progress, cancellationToken).ConfigureAwait(false);
        await WaitForSuccessStatusAsync("download BCT", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a bootloader image with <c>NV3P_CMD_DL_BL</c>: the command carries the length, the load address
    /// and the entry point, the device confirms it with a status, and the payload follows in data packets.
    /// <para>用 <c>NV3P_CMD_DL_BL</c> 下载 bootloader 镜像：命令携带长度、加载地址与入口点，设备先以状态确认，负载
    /// 随后以数据包发送。</para>
    /// </summary>
    /// <param name="bootloader">The bootloader image; empty input is rejected before any byte goes on the wire.
    /// <para>bootloader 镜像；空输入在任何字节上线之前即被拒绝。</para></param>
    /// <param name="loadAddress">Load address written into the command; the device places the image there.
    /// <para>写入命令的加载地址；设备把镜像放到该地址。</para></param>
    /// <param name="entryPoint">Execution entry point written into the command.
    /// <para>写入命令的执行入口点。</para></param>
    /// <param name="progress">Optional sink for transfer progress. <para>可选的传输进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels between chunks. <para>在分块之间取消。</para></param>
    /// <returns>A task that completes once the payload has been transferred and acknowledged.
    /// <para>负载传输并得到确认后完成的任务。</para></returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已释放。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="bootloader"/> is empty or above
    /// <see cref="MaximumBootloaderLength"/>. <para><paramref name="bootloader"/> 为空或超过
    /// <see cref="MaximumBootloaderLength"/>。</para></exception>
    /// <exception cref="TegraProtocolException">The device rejected the command or the transfer failed its checks.
    /// <para>设备拒绝了该命令，或传输未通过校验。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task DownloadBootloaderAsync(ReadOnlyMemory<byte> bootloader, uint loadAddress, uint entryPoint,
        IProgress<TegraNv3pProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bootloader.IsEmpty || bootloader.Length > MaximumBootloaderLength)
        {
            throw new ArgumentException(
                $"A bootloader download carries 1 to {MaximumBootloaderLength} bytes, but {bootloader.Length} were "
                + "supplied.",
                nameof(bootloader));
        }

        var arguments = new byte[(2 * sizeof(uint)) + sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(arguments, (ulong)bootloader.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(sizeof(ulong)), loadAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(sizeof(ulong) + sizeof(uint)), entryPoint);
        await SendCommandAsync(TegraNv3pCommand.DownloadBootloader, arguments, cancellationToken)
            .ConfigureAwait(false);

        // The reference waits for the status right after the command and before the payload, so a device that
        // refuses the length or the addresses rejects the download before any data is transferred.
        // 参考实现在命令之后、负载之前等待状态，因此拒绝长度或地址的设备会在传输任何数据之前拒绝本次下载。
        await WaitForSuccessStatusAsync("download bootloader", cancellationToken).ConfigureAwait(false);
        await SendPayloadAsync(bootloader, "bootloader", progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the status packet the nv3p service initiates with <c>NV3P_CMD_STATUS</c> and acknowledges it.
    /// <para>读取 nv3p 服务以 <c>NV3P_CMD_STATUS</c> 主动发起的状念包并予以确认。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The decoded status, which is returned even when its code is not the success code.
    /// <para>解码出的状态；即使代码不是成功码也会返回。</para></returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已释放。</para></exception>
    /// <exception cref="TegraProtocolException">The packet is not a status command, its argument length is not
    /// <see cref="StatusLength"/>, or its checksum or sequence does not verify.
    /// <para>该包不是状态命令、其参数长度不是 <see cref="StatusLength"/>，或校验和与序号未通过校验。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public async Task<TegraNv3pDeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var header = await ReadPacketHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (header.PacketType != TegraNv3pPacketType.Command)
        {
            await DrainAckAsync(header, cancellationToken).ConfigureAwait(false);
            throw new TegraProtocolException(
                $"Expected the nv3p status command packet, but received packet type 0x{(uint)header.PacketType:X}.");
        }

        var command = new byte[TegraNv3pConstants.CommandPacketSize];
        await ReadExactlyAsync(command, 0, command.Length, "status command", cancellationToken)
            .ConfigureAwait(false);
        var checksum = unchecked(header.Checksum + TegraNv3pConstants.ComputeChecksum(command));
        var argumentLength = BinaryPrimitives.ReadUInt32LittleEndian(command);
        var commandWord = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(sizeof(uint)));
        if ((TegraNv3pCommand)commandWord != TegraNv3pCommand.Status)
        {
            throw new TegraProtocolException(
                $"Expected the nv3p status command 0x{(uint)TegraNv3pCommand.Status:X}, but received command "
                + $"0x{commandWord:X}.");
        }

        if (argumentLength != StatusLength)
        {
            throw new TegraProtocolException(
                $"An nv3p status packet carries {StatusLength} argument bytes, but the device declared "
                + $"{argumentLength}.");
        }

        var arguments = new byte[StatusLength];
        await ReadExactlyAsync(arguments, 0, arguments.Length, "status payload", cancellationToken)
            .ConfigureAwait(false);
        checksum = unchecked(checksum + TegraNv3pConstants.ComputeChecksum(arguments));

        var footer = new byte[TegraNv3pConstants.FooterPacketSize];
        await ReadExactlyAsync(footer, 0, footer.Length, "status checksum", cancellationToken).ConfigureAwait(false);
        VerifyChecksum(checksum, BinaryPrimitives.ReadUInt32LittleEndian(footer), "status packet");

        await SendAckAsync(cancellationToken).ConfigureAwait(false);

        var text = DecodeStatusMessage(arguments);
        var code = BinaryPrimitives.ReadUInt32LittleEndian(arguments.AsSpan(TegraNv3pConstants.StatusStringLength));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(
            arguments.AsSpan(TegraNv3pConstants.StatusStringLength + sizeof(uint)));
        return new TegraNv3pDeviceStatus(text, code, flags);
    }

    /// <summary>
    /// Reports one progress line for a phase that is not itself a transfer, for the flasher that drives this session.
    /// <para>为驱动本会话的烧录器上报一条非传输阶段的进度行。</para>
    /// </summary>
    /// <remarks>
    /// Internal coordination primitive: it only forwards to the caller-supplied sink and neither writes to nor reads
    /// from the transport, so it cannot change wire behaviour or add a packet.
    /// <para>内部协作原语：只转发到调用方提供的接收器，既不写也不读传输，因此不会改变线上行为，也不会额外发包。</para>
    /// </remarks>
    /// <param name="progress">The sink to forward to, or <see langword="null"/> to do nothing.
    /// <para>要转发到的接收器；<see langword="null"/> 时不做任何事。</para></param>
    /// <param name="phase">Phase name the report carries. <para>报告携带的阶段名。</para></param>
    /// <param name="bytesCompleted">Payload bytes completed so far. <para>已完成的负载字节。</para></param>
    /// <param name="bytesTotal">Total payload bytes, or zero when unknown. <para>负载总字节；未知时为零。</para></param>
    /// <param name="message">Human-readable status line. <para>人类可读的状态行。</para></param>
    internal static void Record(IProgress<TegraNv3pProgress>? progress, string phase, long bytesCompleted,
        long bytesTotal, string message)
        => progress?.Report(new TegraNv3pProgress(phase, bytesCompleted, bytesTotal, message));

    /// <summary>
    /// Releases the session without disposing the transport it was given, because the caller owns it and releases
    /// it through the lease that produced it; a second call is a no-op.
    /// <para>释放会话但不释放在构造时传入的传输；传输由调用方经产生它的租约释放。再次调用不做任何事。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private static void VerifyChecksum(uint receivedSum, uint checksum, string what)
    {
        // The referenced checksum is the two's complement of the byte sum, so adding the two must wrap to zero.
        // 参考校验和是字节求和的反码加一，因此两者相加必须回绕为零。
        if (unchecked(receivedSum + checksum) != 0)
        {
            throw new TegraProtocolException(
                $"The {what} failed its checksum: the received checksum 0x{checksum:X8} does not cancel the "
                + $"0x{receivedSum:X8} byte sum.");
        }
    }

    private static uint NegativeChecksum(ReadOnlySpan<byte> buffer) => unchecked(~TegraNv3pConstants.ComputeChecksum(buffer) + 1);

    private static string DecodeStatusMessage(ReadOnlySpan<byte> arguments)
    {
        var text = arguments.Slice(0, TegraNv3pConstants.StatusStringLength);
        var terminator = text.IndexOf((byte)0);
        if (terminator >= 0)
        {
            text = text.Slice(0, terminator);
        }

        // The text is device-controlled bytes; ASCII maps every non-ASCII byte to a replacement instead of letting
        // it decide the encoding of the whole buffer.
        // 该文本是设备控制的字节；ASCII 解码会把每个非 ASCII 字节替换掉，而不是让它决定整个缓冲区的编码。
        return Encoding.ASCII.GetString(text).Trim();
    }

    private static TegraPlatformInfo ParsePlatformInfo(ReadOnlySpan<byte> buffer)
    {
        var chipId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(16, sizeof(ushort)));
        var chipMajor = buffer[18];
        var chipMinor = buffer[19];
        var board = buffer.Slice(56, 4 * sizeof(uint));
        return new TegraPlatformInfo
        {
            UniqueIdLow = BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            UniqueIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(sizeof(ulong), sizeof(ulong))),
            Chip = new TegraChipId(chipId, chipMajor, chipMinor),
            Sku = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(20, sizeof(uint))),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(24, sizeof(uint))),
            BootDevice = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(28, sizeof(uint))),
            OperatingMode = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(32, sizeof(uint))),
            DeviceConfigurationStrap = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(36, sizeof(uint))),
            DeviceConfigurationFuse = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(40, sizeof(uint))),
            SdramConfigurationStrap = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(44, sizeof(uint))),
            Board = new TegraBoardId(
                BinaryPrimitives.ReadUInt32LittleEndian(board),
                BinaryPrimitives.ReadUInt32LittleEndian(board.Slice(sizeof(uint), sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(board.Slice(2 * sizeof(uint), sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(board.Slice(3 * sizeof(uint), sizeof(uint))))
        };
    }

    private async Task SendCommandAsync(TegraNv3pCommand command, ReadOnlyMemory<byte> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Length > MaximumCommandArgumentLength)
        {
            throw new ArgumentException(
                $"An nv3p command carries at most {MaximumCommandArgumentLength} argument bytes.",
                nameof(arguments));
        }

        var covered = TegraNv3pConstants.BasicPacketSize + TegraNv3pConstants.CommandPacketSize + arguments.Length;
        var packet = new byte[covered + TegraNv3pConstants.FooterPacketSize];
        WriteHeader(packet, TegraNv3pPacketType.Command, _sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(TegraNv3pConstants.BasicPacketSize),
            (uint)arguments.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(TegraNv3pConstants.BasicPacketSize + sizeof(uint)),
            (uint)command);
        arguments.Span.CopyTo(packet.AsSpan(TegraNv3pConstants.BasicPacketSize + TegraNv3pConstants.CommandPacketSize));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(covered), NegativeChecksum(packet.AsSpan(0, covered)));

        await WriteAllAsync(packet, cancellationToken).ConfigureAwait(false);
        _sequence++;
        await WaitForAckAsync($"command 0x{(uint)command:X2}", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPayloadAsync(ReadOnlyMemory<byte> payload, string name,
        IProgress<TegraNv3pProgress>? progress, CancellationToken cancellationToken)
    {
        var reported = 0L;
        var offset = 0;
        Record(progress, "download", 0, payload.Length, $"Sending the {name} payload ({payload.Length} bytes).");
        while (offset < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(MaximumDownloadChunkLength, payload.Length - offset);
            await SendDataAsync(payload.Slice(offset, length), cancellationToken).ConfigureAwait(false);
            offset += length;
            if (offset - reported >= 256 * 1024 || offset == payload.Length)
            {
                reported = offset;
                Record(progress, "download", offset, payload.Length,
                    $"Sent {offset} of {payload.Length} {name} bytes.");
            }
        }
    }

    private async Task SendDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length == 0 || data.Length > TegraNv3pConstants.MaximumDataPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Length,
                $"A data packet carries 1 to {TegraNv3pConstants.MaximumDataPayloadLength} payload bytes.");
        }

        var header = new byte[TegraNv3pConstants.BasicPacketSize + TegraNv3pConstants.DataPacketSize];
        WriteHeader(header, TegraNv3pPacketType.Data, _sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(TegraNv3pConstants.BasicPacketSize),
            (uint)data.Length);

        // The footer is the negation of the summed header and payload; computing it from the parts avoids copying the
        // payload into the frame just to checksum it a second time.
        // 尾部是头部与负载求和的反码；由分段计算可直接得出，无需为再算一次校验和而把负载复制进帧。
        var sum = unchecked(
            TegraNv3pConstants.ComputeChecksum(header) + TegraNv3pConstants.ComputeChecksum(data.Span));
        var footer = new byte[TegraNv3pConstants.FooterPacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, unchecked(~sum + 1));

        await WriteAllAsync(header, cancellationToken).ConfigureAwait(false);
        await WriteAllAsync(data, cancellationToken).ConfigureAwait(false);
        await WriteAllAsync(footer, cancellationToken).ConfigureAwait(false);
        _sequence++;
        await WaitForAckAsync("data packet", cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveDataAsync(byte[] destination, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receivedPayloadRemaining == 0)
            {
                var header = await ReadPacketHeaderAsync(cancellationToken).ConfigureAwait(false);
                if (header.PacketType != TegraNv3pPacketType.Data)
                {
                    await DrainAckAsync(header, cancellationToken).ConfigureAwait(false);
                    throw new TegraProtocolException(
                        $"Expected an nv3p data packet, but received packet type 0x{(uint)header.PacketType:X}.");
                }

                var lengthBuffer = new byte[TegraNv3pConstants.DataPacketSize];
                await ReadExactlyAsync(lengthBuffer, 0, lengthBuffer.Length, "data length", cancellationToken)
                    .ConfigureAwait(false);
                var declared = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
                if (declared == 0 || declared > TegraNv3pConstants.MaximumDataPayloadLength)
                {
                    throw new TegraProtocolException(
                        $"The device declared a {declared}-byte data payload, outside the 1.."
                        + $"{TegraNv3pConstants.MaximumDataPayloadLength} byte range.");
                }

                _receivedChecksum = unchecked(header.Checksum + TegraNv3pConstants.ComputeChecksum(lengthBuffer));
                _receivedPayloadRemaining = declared;
            }

            var chunk = (int)Math.Min((uint)(destination.Length - filled), _receivedPayloadRemaining);
            await ReadExactlyAsync(destination, filled, chunk, "data payload", cancellationToken).ConfigureAwait(false);
            _receivedChecksum = unchecked(
                _receivedChecksum + TegraNv3pConstants.ComputeChecksum(destination.AsSpan(filled, chunk)));
            filled += chunk;
            _receivedPayloadRemaining -= (uint)chunk;
            if (_receivedPayloadRemaining == 0)
            {
                var footer = new byte[TegraNv3pConstants.FooterPacketSize];
                await ReadExactlyAsync(footer, 0, footer.Length, "data checksum", cancellationToken)
                    .ConfigureAwait(false);
                VerifyChecksum(_receivedChecksum, BinaryPrimitives.ReadUInt32LittleEndian(footer), "data packet");
                _receivedChecksum = 0;
                await SendAckAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Nv3pPacketHeader> ReadPacketHeaderAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[TegraNv3pConstants.BasicPacketSize];
        await ReadExactlyAsync(buffer, 0, buffer.Length, "packet header", cancellationToken).ConfigureAwait(false);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (version != TegraNv3pConstants.ProtocolVersion)
        {
            throw new TegraProtocolException(
                $"The device answered with nv3p version 0x{version:X8}, but this host speaks "
                + $"0x{TegraNv3pConstants.ProtocolVersion:X8}.");
        }

        var packetType = (TegraNv3pPacketType)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sizeof(uint)));
        if (packetType is not (TegraNv3pPacketType.Command or TegraNv3pPacketType.Data
            or TegraNv3pPacketType.Encrypted or TegraNv3pPacketType.Ack or TegraNv3pPacketType.Nack))
        {
            throw new TegraProtocolException($"The device answered with unknown nv3p packet type 0x{(uint)packetType:X}.");
        }

        _receivedSequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2 * sizeof(uint)));
        return new Nv3pPacketHeader(packetType, _receivedSequence, TegraNv3pConstants.ComputeChecksum(buffer));
    }

    private async Task WaitForAckAsync(string what, CancellationToken cancellationToken)
    {
        var header = await ReadPacketHeaderAsync(cancellationToken).ConfigureAwait(false);
        var sum = header.Checksum;
        uint? nackCode = null;
        if (header.PacketType == TegraNv3pPacketType.Nack)
        {
            // The negative acknowledgement carries a four-byte code between the header and the checksum, and that
            // code is part of the covered bytes.
            // 否定应答在包头与校验和之间多带一个四字节代码，该代码也属于被校验的字节。
            var codeBuffer = new byte[TegraNv3pConstants.NackPacketSize];
            await ReadExactlyAsync(codeBuffer, 0, codeBuffer.Length, "nack code", cancellationToken)
                .ConfigureAwait(false);
            nackCode = BinaryPrimitives.ReadUInt32LittleEndian(codeBuffer);
            sum = unchecked(sum + TegraNv3pConstants.ComputeChecksum(codeBuffer));
        }
        else if (header.PacketType != TegraNv3pPacketType.Ack)
        {
            await DrainAckAsync(header, cancellationToken).ConfigureAwait(false);
            throw new TegraProtocolException(
                $"Expected an acknowledgement for the {what}, but received packet type 0x{(uint)header.PacketType:X}.");
        }

        var footer = new byte[TegraNv3pConstants.FooterPacketSize];
        await ReadExactlyAsync(footer, 0, footer.Length, "acknowledgement checksum", cancellationToken)
            .ConfigureAwait(false);
        VerifyChecksum(sum, BinaryPrimitives.ReadUInt32LittleEndian(footer), $"acknowledgement of the {what}");

        if (header.Sequence != _sequence - 1)
        {
            throw new TegraProtocolException(
                $"The device acknowledged sequence {header.Sequence}, but the {what} carried sequence "
                + $"{_sequence - 1}.");
        }

        if (nackCode is { } code)
        {
            throw new TegraProtocolException($"The device rejected the {what}.", code, null);
        }
    }

    private async Task WaitForSuccessStatusAsync(string what, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsOk)
        {
            throw new TegraProtocolException(
                $"The device reported status 0x{status.Code:X8} after the {what}.",
                status.Code,
                status.Message);
        }
    }

    private async Task DrainAckAsync(Nv3pPacketHeader header, CancellationToken cancellationToken)
    {
        if (header.PacketType == TegraNv3pPacketType.Nack)
        {
            var codeBuffer = new byte[TegraNv3pConstants.NackPacketSize];
            await ReadExactlyAsync(codeBuffer, 0, codeBuffer.Length, "nack code", cancellationToken)
                .ConfigureAwait(false);
        }
        else if (header.PacketType != TegraNv3pPacketType.Ack)
        {
            return;
        }

        var footer = new byte[TegraNv3pConstants.FooterPacketSize];
        await ReadExactlyAsync(footer, 0, footer.Length, "acknowledgement checksum", cancellationToken)
            .ConfigureAwait(false);
    }

    private Task SendAckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packet = new byte[TegraNv3pConstants.BasicPacketSize + TegraNv3pConstants.AckPacketSize
            + TegraNv3pConstants.FooterPacketSize];
        WriteHeader(packet, TegraNv3pPacketType.Ack, _receivedSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(TegraNv3pConstants.BasicPacketSize + TegraNv3pConstants.AckPacketSize),
            NegativeChecksum(packet.AsSpan(0, TegraNv3pConstants.BasicPacketSize)));
        return WriteAllAsync(packet, cancellationToken);
    }

    private static void WriteHeader(Span<byte> packet, TegraNv3pPacketType packetType, uint sequence)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, TegraNv3pConstants.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(sizeof(uint)), (uint)packetType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(2 * sizeof(uint)), sequence);
    }

    private async Task ReadExactlyAsync(byte[] buffer, int offset, int length, string what,
        CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await Transport.ReadAsync(buffer, offset + filled, length - filled, _timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (read < 0 || read > length - filled)
            {
                throw new TegraProtocolException(
                    $"The transport reported {read} bytes for a {length - filled}-byte {what} request.");
            }

            if (read == 0)
            {
                throw new TegraProtocolException(
                    $"The device stopped sending in the middle of the {what}: {filled} of {length} bytes arrived.");
            }

            filled += read;
        }
    }

    private async Task WriteAllAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var array = data.ToArray();
        var written = 0;
        while (written < array.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await Transport.WriteAsync(array, written, array.Length - written, _timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (count <= 0 || count > array.Length - written)
            {
                throw new TegraProtocolException(
                    $"The transport reported {count} bytes written for a {array.Length - written}-byte request.");
            }

            written += (int)count;
        }
    }

    private readonly record struct Nv3pPacketHeader(TegraNv3pPacketType PacketType, uint Sequence, uint Checksum);
}