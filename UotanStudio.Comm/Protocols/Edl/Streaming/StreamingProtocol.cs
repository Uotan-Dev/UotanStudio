using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>Implements the Qualcomm Streaming DLOAD protocol, the pre-Firehose command-mode flash transport over DIAG packets. The session enters it after Sahara exec 0x05, negotiates with a hello exchange, optionally programs flash in 1024-byte chunks and finalizes sections with qclose.
/// <para>实现 Qualcomm Streaming DLOAD 协议，即 Firehose 之前经 DIAG 数据包承载的命令模式刷写传输。Sahara exec 0x05 后进入该协议，经 hello 交换协商，可选按 1024 字节分块编程闪存，并以 qclose 收尾。</para></summary>
/// <remarks>Operations on one instance are serialized through the operation gate; the transport must not be shared with another protocol instance while an operation is in progress.
/// <para>同一实例上的操作经操作门串行化；操作进行中不得与其他协议实例共享传输层。</para></remarks>
/// <param name="transport">The device transport handed over by the Sahara command-mode switch.
/// <para>由 Sahara 命令模式切换交接的设备传输层。</para></param>
/// <param name="timeoutMs">Optional timeout in milliseconds; <see langword="null"/> selects <see cref="EdlConstants.DefaultCommandTimeoutMs"/>.
/// <para>可选的超时（毫秒）；<see langword="null"/> 选择 <see cref="EdlConstants.DefaultCommandTimeoutMs"/>。</para></param>
public sealed partial class StreamingProtocol(ICommTransport transport, int? timeoutMs = null) : IDisposable
{
    private const int DisposalGateTimeoutMs = 250;

    /// <summary>
    /// The ASCII marker this host writes in front of every section name: <c>0:</c> followed by the
    /// name.
    /// <para>本主机在每个段名前写入的 ASCII 标记：<c>0:</c> 加段名。</para>
    /// </summary>
    private const string SectionNamePrefix = "0:";

    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _timeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly byte[] _rxBuffer = new byte[StreamingConstants.MaxPacketBytes];
    private volatile bool _disposed;
    private volatile bool _operationGateRetired;
    private StreamingMemoryLinkMode _linkMode = StreamingMemoryLinkMode.Auto;

    /// <summary>
    /// Gets the device description parsed from the most recent successful hello exchange, or
    /// <see langword="null"/> before <see cref="HelloAsync"/> succeeds.
    /// <para>获取从最近一次成功 hello 交换解析出的设备描述；<see cref="HelloAsync"/> 成功前为
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <value>The negotiated device information. <para>已协商的设备信息。</para></value>
    public StreamingDeviceInfo? Device { get; private set; }

    /// <summary>
    /// Gets the memory link mode this session currently uses for peek and poke traffic.
    /// <para>获取本会话当前用于 peek/poke 流量的内存链接模式。</para>
    /// </summary>
    /// <value>The negotiated mode; <see cref="StreamingMemoryLinkMode.Auto"/> until
    /// <see cref="IdentifyChipsetAsync"/> or an explicit read resolves it.
    /// <para>已协商的模式；在 <see cref="IdentifyChipsetAsync"/> 或显式读取解析之前为
    /// <see cref="StreamingMemoryLinkMode.Auto"/>。</para></value>
    public StreamingMemoryLinkMode NegotiatedLinkMode => _linkMode;

    /// <summary>
    /// Raised for log messages.
    /// <para>日志消息事件。</para>
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <summary>Sends the host hello with <see cref="StreamingConstants.HostMagic"/> and parses the target's device description. One mismatched response triggers a single resend before the exchange is declared failed.
    /// <para>随 <see cref="StreamingConstants.HostMagic"/> 发送主机 hello 并解析目标的设备描述。首个不匹配的响应会触发一次重发，之后才判定交换失败。</para></summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The parsed device description, also cached in <see cref="Device"/>.
    /// <para>解析出的设备描述，同时缓存到 <see cref="Device"/>。</para></returns>
    /// <exception cref="StreamingException">Thrown when the target reports an unrecognized flash device, answers with an undecodable frame, or never sends the hello response identifier.
    /// <para>目标报告无法识别的闪存设备、应答帧无法解码，或从未发送 hello 响应号时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<StreamingDeviceInfo> HelloAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await HelloOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Negotiates the trusted or untrusted security state with DIAG command 0x17 and reports whether the target acknowledged with 0x18.
    /// <para>用 DIAG 命令 0x17 协商受信或不受信的安全状态，并报告目标是否以 0x18 确认。</para></summary>
    /// <param name="trusted">Pass <see langword="true"/> for the trusted mode byte 0x01, <see langword="false"/> for 0x00.
    /// <para>受信模式传 <see langword="true"/>（模式字节 0x01）， 否则传 <see langword="false"/>（0x00）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the target acknowledged the negotiated state.
    /// <para>目标确认所协商状态时为 <see langword="true"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<bool> SecureModeAsync(bool trusted = true, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await SecureModeOwnedAsync(trusted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the DIAG close (qclose) command 0x15 and reports whether the target answered with
    /// the close response identifier 0x16.
    /// <para>发送 DIAG 关闭（qclose）命令 0x15，并报告目标是否以关闭响应号 0x16 应答
    /// 。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the close was acknowledged.
    /// <para>关闭得到确认时为 <see langword="true"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await CloseOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends the DIAG reset command 0x0B.
    /// <para>发送 DIAG 复位命令 0x0B。</para>A target that goes silent immediately after the command is tolerated; the session simply stops being usable once the loader leaves the streaming protocol.<para>命令后目标立即静默是可以容忍的；loader 一旦离开流式协议，会话即不再可用。</para></summary>
    /// <param name="confirm">Pass <see langword="true"/> to confirm that resetting the streaming session is intended.
    /// <para>传 <see langword="true"/> 以确认确实要复位流式会话。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is <see langword="false"/>; the refusal happens before the operation lease and before any byte reaches the wire.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/> 时抛出； 拒绝发生在取得操作租约之前，任何字节都不落到线上。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task ResetAsync(bool confirm, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException(
                "Resetting the streaming session requires explicit confirmation.", nameof(confirm));
        }

        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        var frame = await SendDiagOwnedAsync([StreamingConstants.DiagResetCommand],
            verifyResponseCrc: false, tolerateNoResponse: true, cancellationToken).ConfigureAwait(false);
        Log(frame.Length == 0
            ? "Streaming reset sent; the target answered nothing."
            : "Streaming reset acknowledged.");
    }

    /// <summary>
    /// Performs the hello exchange on an already-leased session.
    /// <para>在已持有租约的会话上执行 hello 交换。</para>
    /// </summary>
    private async Task<StreamingDeviceInfo> HelloOwnedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var magic = Encoding.ASCII.GetBytes(StreamingConstants.HostMagic);
        var request = new byte[1 + magic.Length + 5];
        request[0] = StreamingConstants.DiagHelloCommand;
        magic.CopyTo(request, 1);
        request[33] = StreamingConstants.DiagHostVersion;
        request[34] = 0x23;
        request[35] = 0x23;
        request[36] = 0x23;
        request[37] = 0x20;

        var reply = await SendDiagOwnedAsync(request, verifyResponseCrc: false,
            tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
        if (reply.Length == 0 || reply[0] != StreamingConstants.DiagHelloResponse)
        {
            reply = await SendDiagOwnedAsync(request, verifyResponseCrc: false,
                tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
        }

        if (reply.IndexOf("Unrecognized flash device"u8) >= 0)
        {
            Log("Unrecognized flash device; patching the loader to match the flash or using a different loader is required.");
            await SendDiagOwnedAsync([StreamingConstants.DiagResetCommand], verifyResponseCrc: false,
                tolerateNoResponse: true, cancellationToken).ConfigureAwait(false);
            throw new StreamingException(EdlErrorCode.TargetNak,
                "The streaming target reported an unrecognized flash device and was reset.");
        }

        if (reply.Length == 0 || reply[0] != StreamingConstants.DiagHelloResponse
            || !StreamingDeviceInfo.TryParse(reply, out var info) || info is null)
        {
            throw new StreamingException(EdlErrorCode.TargetNak,
                "The streaming target did not answer the host hello with a decodable response.");
        }

        Device = info;
        Log($"Streaming hello acknowledged, protocol version {info.Version}.");
        return info;
    }

    /// <summary>
    /// Runs the secure-mode negotiation on an already-leased session.
    /// <para>在已持有租约的会话上执行安全模式协商。</para>
    /// </summary>
    private async Task<bool> SecureModeOwnedAsync(bool trusted, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var reply = await SendDiagOwnedAsync(
            [StreamingConstants.DiagSecureCommand, trusted ? (byte)0x01 : (byte)0x00],
            verifyResponseCrc: false, tolerateNoResponse: true, cancellationToken).ConfigureAwait(false);
        var acknowledged = reply.Length > 0 && reply[0] == StreamingConstants.DiagSecureResponse;
        Log(acknowledged
            ? $"Streaming secure mode {(trusted ? "trusted" : "untrusted")} acknowledged."
            : "Streaming secure mode negotiation was not acknowledged.");
        return acknowledged;
    }

    /// <summary>
    /// Runs the qclose exchange on an already-leased session.
    /// <para>在已持有租约的会话上执行 qclose 交换。</para>
    /// </summary>
    private async Task<bool> CloseOwnedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var reply = await SendDiagOwnedAsync([StreamingConstants.DiagCloseCommand],
            verifyResponseCrc: true, tolerateNoResponse: true, cancellationToken).ConfigureAwait(false);
        return reply.Length > 0 && reply[0] == StreamingConstants.DiagCloseResponse;
    }

    /// <summary>
    /// Frames one DIAG command with the CRC appended on send (the no-CRC variants only skip
    /// verification of the reply), writes it, and reads one framed reply.
    /// <para>封装一条 DIAG 命令时总在发送侧附加 CRC（no-CRC 变体只跳过对回包的校验），写出后读取
    /// 一个封装回包。</para>
    /// </summary>
    private async Task<byte[]> SendDiagOwnedAsync(byte[] command, bool verifyResponseCrc,
        bool tolerateNoResponse, CancellationToken cancellationToken)
    {
        var frame = DiagHdlcCodec.Encode(command, withCrc: true);
        var written = await _transport.WriteAsync(frame, 0, frame.Length, _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (written != frame.Length)
        {
            throw new StreamingException(EdlErrorCode.ConnectionLost,
                "The transport accepted only part of the DIAG request frame.");
        }

        try
        {
            return await ReceiveDiagFrameOwnedAsync(verifyResponseCrc, cancellationToken).ConfigureAwait(false);
        }
        catch (EdlException ex) when (tolerateNoResponse && ex.ErrorCode is EdlErrorCode.ReadPortTimeout)
        {
            return [];
        }
    }

    /// <summary>
    /// Accumulates transport reads until the trailing HDLC flag arrives: reading continues while the
    /// last byte is not <c>0x7E</c>, then exactly one framed payload is decoded. A whole-packet
    /// budget of <c>_timeoutMs</c> bounds the accumulation.
    /// <para>持续累积传输读取直到尾部的 HDLC 标志到达：末字节不是 <c>0x7E</c> 就继续读，随后解码恰好
    /// 一个封装负载。以 <c>_timeoutMs</c> 为整包预算约束累积过程。</para>
    /// </summary>
    private async Task<byte[]> ReceiveDiagFrameOwnedAsync(bool verifyCrc, CancellationToken cancellationToken)
    {
        var total = 0;
        var deadline = Environment.TickCount64 + _timeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0)
            {
                throw new StreamingException(EdlErrorCode.ReadPortTimeout,
                    "The streaming DIAG reply deadline expired.");
            }

            var read = await _transport.ReadAsync(_rxBuffer, total, _rxBuffer.Length - total,
                (int)Math.Min(int.MaxValue, remainingMs), cancellationToken).ConfigureAwait(false);
            if (read < 0 || read > _rxBuffer.Length - total)
            {
                throw new StreamingException(EdlErrorCode.ConnectionLost,
                    "The transport returned an invalid DIAG receive length.");
            }

            if (read == 0)
            {
                if (total > 0 && _rxBuffer[total - 1] == DiagHdlcCodec.FrameFlag)
                {
                    break;
                }

                continue;
            }

            total += read;
            if (_rxBuffer[total - 1] == DiagHdlcCodec.FrameFlag)
            {
                break;
            }

            if (total == _rxBuffer.Length)
            {
                throw new StreamingException(EdlErrorCode.InvalidParameter,
                    "The streaming DIAG reply exceeded the maximum packet length.");
            }
        }

        if (!DiagHdlcCodec.TryDecode(_rxBuffer.AsSpan(0, total), withCrc: true, verifyCrc, out var payload)
            || payload is null)
        {
            throw new StreamingException(EdlErrorCode.InvalidParameter,
                "The streaming DIAG reply frame is malformed.");
        }

        return payload;
    }

    /// <summary>
    /// Logs a message via the <see cref="LogMessage"/> event.
    /// <para>通过 <see cref="LogMessage"/> 事件记录消息。</para>
    /// </summary>
    private void Log(string message) => LogMessage?.Invoke(this, message);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> when the protocol has been disposed.
    /// <para>协议已释放时抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StreamingProtocol));
        }
    }

    /// <summary>
    /// Reserves the transport for one streaming operation, following the same operation-gate
    /// pattern as the other EDL protocol classes.
    /// <para>为一次流式操作预留传输，遵循与其他 EDL 协议类相同的操作门模式。</para>
    /// </summary>
    private async ValueTask<FirehoseProtocol.OperationLease> EnterOperationAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return new FirehoseProtocol.OperationLease(_operationGate);
        }
        catch
        {
            ReleaseGate(_operationGate);
            throw;
        }
    }

    /// <summary>
    /// Returns one permit to the operation gate, tolerating the window where the protocol was
    /// disposed before the caller finished.
    /// <para>向操作门归还一个许可，并容忍所属协议在调用方结束前已被释放的窗口。</para>
    /// </summary>
    private static void ReleaseGate(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Disposes the protocol and retires the operation gate with the same bounded-wait discipline
    /// the Firehose protocol uses: an in-flight transfer is never stranded by disposal.
    /// <para>释放协议并按 Firehose 协议的有限等待准则归还操作门：在途传输永远不会被释放卡住。</para>
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (_operationGateRetired)
        {
            return;
        }

        try
        {
            if (!_operationGate.Wait(DisposalGateTimeoutMs))
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        ReleaseGate(_operationGate);
        _operationGate.Dispose();
        _operationGateRetired = true;
    }
}

/// <summary>The device description the streaming target returns in its hello response: version fields, the preferred block size, the base flash address, the flash identification string, the window size, the sector table, and the feature bits.
/// <para>流式目标在 hello 响应中返回的设备描述：版本字段、首选块大小、闪存基地址、闪存识别串、 窗口大小、扇区表与特性位。</para></summary>
/// <param name="Command">The DIAG response identifier, always <see cref="StreamingConstants.DiagHelloResponse"/> for a parsed hello.
/// <para>DIAG 响应号；解析成功的 hello 恒为 <see cref="StreamingConstants.DiagHelloResponse"/>。</para></param>
/// <param name="Version">The protocol version byte the target reports.
/// <para>目标上报的协议版本字节。</para></param>
/// <param name="CompatibleVersion">The compatible-version byte the target reports.
/// <para>目标上报的兼容版本字节。</para></param>
/// <param name="MaxPreferredBlockSize">The maximum preferred block size in bytes.
/// <para>最大首选块大小（字节）。</para></param>
/// <param name="BaseFlashAddress">The base flash address reported by the target.
/// <para>目标上报的闪存基地址。</para></param>
/// <param name="FlashId">The raw flash identification string bytes.<para>原始闪存识别串字节。</para></param>
/// <param name="WindowSize">The reported window size in sectors.<para>上报的窗口大小（扇区数）。</para></param>
/// <param name="NumberOfSectors">The number of sector-size entries in the table.
/// <para>扇区大小表中的条目数。</para></param>
/// <param name="SectorSizes">The sector size table entries the target advertised.
/// <para>目标通告的扇区大小表条目。</para></param>
/// <param name="FeatureBits">The single feature-bits byte following the sector table.
/// <para>扇区表之后的单字节特性位。</para></param>
public sealed record StreamingDeviceInfo(
    byte Command,
    byte Version,
    byte CompatibleVersion,
    uint MaxPreferredBlockSize,
    uint BaseFlashAddress,
    byte[] FlashId,
    ushort WindowSize,
    ushort NumberOfSectors,
    uint[] SectorSizes,
    byte FeatureBits)
{
    /// <summary>
    /// Parses the fixed hello layout, then the variable flash-id and sector tables, bounding
    /// every device-supplied length before reading it. Offsets assume the leading HDLC flag has been
    /// normalized away.
    /// <para>先解析固定 hello 布局，再解析变长的闪存 ID 与扇区表，并在读取任何由设备提供的长度
    /// 之前先做边界检查。偏移以剥掉前导 HDLC 标志为前提。</para>
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> reply, out StreamingDeviceInfo? info)
    {
        info = null;
        const int FixedHeaderBytes = 1 + 32 + 1 + 1 + 4 + 4 + 1;
        if (reply.Length < FixedHeaderBytes || reply[0] != StreamingConstants.DiagHelloResponse)
        {
            return false;
        }

        var flashIdLength = reply[43];
        var flashIdEnd = checked(44 + flashIdLength);
        if (reply.Length < flashIdEnd)
        {
            return false;
        }

        var flashId = reply[44..flashIdEnd].ToArray();
        var tableStart = flashIdEnd;
        if (reply.Length < tableStart + 4)
        {
            return false;
        }

        var windowSize = BinaryPrimitives.ReadUInt16LittleEndian(reply[tableStart..]);
        var numberOfSectors = BinaryPrimitives.ReadUInt16LittleEndian(reply[(tableStart + 2)..]);
        if (numberOfSectors > StreamingConstants.MaxPacketBytes / 4)
        {
            return false;
        }

        var sectorEnd = checked(tableStart + 4 + (4 * numberOfSectors));
        if (reply.Length < sectorEnd + 1)
        {
            return false;
        }

        var sectorSizes = new uint[numberOfSectors];
        for (var index = 0; index < numberOfSectors; index++)
        {
            sectorSizes[index] = BinaryPrimitives.ReadUInt32LittleEndian(reply[(tableStart + 4 + (4 * index))..]);
        }

        info = new StreamingDeviceInfo(
            Command: reply[0],
            Version: reply[33],
            CompatibleVersion: reply[34],
            MaxPreferredBlockSize: BinaryPrimitives.ReadUInt32LittleEndian(reply[35..]),
            BaseFlashAddress: BinaryPrimitives.ReadUInt32LittleEndian(reply[39..]),
            FlashId: flashId,
            WindowSize: windowSize,
            NumberOfSectors: numberOfSectors,
            SectorSizes: sectorSizes,
            FeatureBits: reply[sectorEnd]);
        return true;
    }

    /// <summary>
    /// Resolves the sector size the target itself advertised in its hello response, so that no
    /// caller has to assume a fixed one.
    /// <para>解析目标在 hello 响应中自己通告的扇区尺寸，使调用方无需假设固定值。</para>
    /// </summary>
    /// <remarks>
    /// This session carries no chipset table, so the advertised table is the single geometry the
    /// loader reports. The table must name one size that is a power of two within
    /// <see cref="StreamingConstants.MinReportedSectorBytes"/> to
    /// <see cref="StreamingConstants.MaxReportedSectorBytes"/>: an empty table, several distinct sizes,
    /// or an implausible value is refused instead of guessed, because every derived byte offset, length
    /// and block count scales with it.
    /// <para>本会话没有芯片表，因此该通告表就是 loader 上报的唯一几何信息。表内必须只有一种尺寸，
    /// 且该尺寸为 2 的幂并落在 <see cref="StreamingConstants.MinReportedSectorBytes"/> 到
    /// <see cref="StreamingConstants.MaxReportedSectorBytes"/> 之间：空表、多种不同尺寸或不成理的值
    /// 一律拒绝而非臆测，因为由此派生的每个字节偏移、长度与块数都会随之缩放。</para>
    /// </remarks>
    /// <param name="sectorBytes">The reported sector size in bytes, or 0 when nothing was
    /// resolved. <para>上报的扇区字节数；未能解析时为 0。</para></param>
    /// <returns><see langword="true"/> when one plausible sector size was reported.
    /// <para>上报了唯一且合理的扇区尺寸时为 <see langword="true"/>。</para></returns>
    public bool TryGetReportedSectorBytes(out int sectorBytes)
    {
        sectorBytes = 0;
        if (SectorSizes.Length == 0)
        {
            return false;
        }

        var reported = SectorSizes[0];
        foreach (var size in SectorSizes)
        {
            if (size != reported)
            {
                return false;
            }
        }

        if (!BitOperations.IsPow2(reported)
            || reported < StreamingConstants.MinReportedSectorBytes
            || reported > StreamingConstants.MaxReportedSectorBytes)
        {
            return false;
        }

        sectorBytes = (int)reported;
        return true;
    }
}