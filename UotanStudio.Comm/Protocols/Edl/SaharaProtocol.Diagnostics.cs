using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Diagnostic Sahara command-mode exec commands beyond identity: switch-to-dmss/stream-dload, debug
/// data, the supported-command list and training data.
/// <para>身份查询之外的 Sahara 命令模式诊断 exec 命令：切换 DMSS/流式下载、调试数据、
/// 支持命令列表与训练数据。</para>
/// </summary>
/// <remarks>
/// Each exchange below is a <c>cmd_exec</c> exchange: a 12-byte EXEC_COMMAND request, the response
/// echoing the command together with the payload length, a 12-byte EXEC_DATA request, and then the
/// payload bytes. The command mode they require is negotiated by <see cref="EnterCommandModeAsync"/>.
/// <para>下列每次交换都是 <c>cmd_exec</c> 交换：12 字节的 EXEC_COMMAND 请求、回显命令与负载长度的
/// 响应、12 字节的 EXEC_DATA 请求，随后就是负载字节。这些命令所要求的命令模式由
/// <see cref="EnterCommandModeAsync"/> 协商。</para>
/// </remarks>
public sealed partial class SaharaProtocol
{
    private bool _commandModeReady;

    /// <summary>
    /// Gets whether this instance has already answered a command-mode HELLO and consumed the target's
    /// COMMAND_READY, so exec commands may be sent without reading another HELLO.
    /// <para>获取本实例是否已经应答过命令模式 HELLO 并消费了目标的 COMMAND_READY，从而可以在不再读取
    /// HELLO 的情况下发送 exec 命令。</para>
    /// </summary>
    /// <value><see langword="true"/> between a successful <see cref="EnterCommandModeAsync"/> and the
    /// next mode switch, which clears it. <see cref="SwitchToDmssDloadAsync(CancellationToken)"/> and
    /// <see cref="SwitchToStreamDloadAsync(CancellationToken)"/> hand the transport to another protocol,
    /// so they clear it as well.
    /// <para>在成功执行 <see cref="EnterCommandModeAsync"/> 之后、直到下一次会清除该标志的模式切换之前为
    /// <see langword="true"/>。<see cref="SwitchToDmssDloadAsync(CancellationToken)"/> 与
    /// <see cref="SwitchToStreamDloadAsync(CancellationToken)"/> 会把传输交给另一种协议，因此同样会清除
    /// 该标志。</para></value>
    public bool IsCommandModeReady => _commandModeReady;

    /// <summary>
    /// Drops this instance's record of a negotiated command mode so the next diagnostic has to read the
    /// target's HELLO again.
    /// <para>丢弃本实例已协商命令模式的记录，让下一次诊断必须重新读取目标的 HELLO。</para>
    /// </summary>
    /// <remarks>
    /// A refused or half-finished exec exchange already consumed the buffered HELLO, and keeping the
    /// ready flag would send commands over a link that was never negotiated.
    /// <para>一次被拒绝或半途失败的 exec 交换已经消费掉缓冲的 HELLO，若继续沿用就绪标志就会在没有协商的
    /// 链路上直接发命令。</para>
    /// </remarks>
    public void ResetCommandModeNegotiation() => _commandModeReady = false;

    /// <summary>Answers the target's HELLO in command mode and consumes the COMMAND_READY that follows, the exchange required before any exec command.
    /// <para>以命令模式应答目标的 HELLO 并消费其后的 COMMAND_READY，这是任何 exec 命令之前必需的交换。</para></summary>
    /// <remarks>The HELLO is read exactly once per link, so the call is idempotent and reports <see cref="SaharaMode.Command"/> immediately when <see cref="IsCommandModeReady"/> is already set; a caller that runs several exec commands on one link must therefore reuse one instance. A target already speaking Firehose XML, or one whose HELLO reports image-transfer or memory-debug mode, is refused before any byte is written, because answering with the wrong mode would desynchronize a link the caller still needs.
    /// <para>一条链路上的 HELLO 只会被读取一次，因此本调用是幂等的：当 <see cref="IsCommandModeReady"/> 已经置位时立即返回 <see cref="SaharaMode.Command"/>；在同一链路上运行多条 exec 命令的调用方必须复用 同一个实例。已经在使用 Firehose XML 的目标，或 HELLO 声明镜像传输/内存调试模式的目标，都会在写出任何 字节之前被拒绝，因为以错误的模式应答会打乱调用方仍需要的链路。</para></remarks>
    /// <param name="cancellationToken">Cancellation token checked before every packet.
    /// <para>在每个数据包之前检查的取消令牌。</para></param>
    /// <returns>The mode the target's HELLO requested and this host acknowledged, always <see cref="SaharaMode.Command"/> on success.
    /// <para>目标 HELLO 请求且本主机已确认的模式，成功时始终为 <see cref="SaharaMode.Command"/>。</para></returns>
    /// <exception cref="ObjectDisposedException">The protocol instance has been disposed.
    /// <para>协议实例已被释放。</para></exception>
    /// <exception cref="SaharaException">The first packet is Firehose XML, the HELLO is malformed, the HELLO does not report <see cref="SaharaMode.Command"/>, or the target did not answer the command-mode HELLO_RESPONSE with a COMMAND_READY.
    /// <para>首个包是 Firehose XML、HELLO 畸形、HELLO 未声明 <see cref="SaharaMode.Command"/>，或目标没有以 COMMAND_READY 应答命令模式的 HELLO_RESPONSE。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<SaharaMode> EnterCommandModeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_commandModeReady)
        {
            return SaharaMode.Command;
        }

        var helloBytes = await ReadExactAsync(
            Marshal.SizeOf<SaharaHelloPacket>(), _timeoutMs, cancellationToken).ConfigureAwait(false);
        if (LooksLikeFirehoseXml(helloBytes))
        {
            throw new SaharaException(
                "The device answered with Firehose XML; Sahara exec commands require a target in command mode.");
        }

        var hello = BytesToStruct<SaharaHelloPacket>(helloBytes);
        ValidateHello(hello);
        if (hello.Mode != SaharaMode.Command)
        {
            throw new SaharaException($"Sahara exec commands require a command-mode HELLO, received {hello.Mode}.");
        }

        NegotiatedProtocolVersion = Math.Min(hello.Version, EdlConstants.SaharaHostMaxProtocolVersion);
        await WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = NegotiatedProtocolVersion.Value,
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Status = 0,
            Mode = SaharaMode.Command
        }, cancellationToken).ConfigureAwait(false);

        var readyBytes = await ReadExactAsync(
            Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken).ConfigureAwait(false);
        var ready = BytesToStruct<SaharaPacketHeader>(readyBytes);
        ValidateCommand(ready.Command, SaharaCommand.CommandReady, ready.Length,
            (uint)Marshal.SizeOf<SaharaPacketHeader>());
        _commandModeReady = true;
        Log("Device answered COMMAND_READY; Sahara is in command mode for exec diagnostics.");
        HandshakeComplete?.Invoke(this, SaharaMode.Command);
        return SaharaMode.Command;
    }

    /// <summary>Sends the command-mode no-operation exec command and confirms the target answers it.
    /// <para>发送命令模式空操作 exec 命令并确认目标应答。</para></summary>
    /// <remarks>The session must sit in Sahara command mode: the caller answers the HELLO with <see cref="SaharaMode.Command"/> — see <see cref="EnterCommandModeAsync(CancellationToken)"/> — before issuing exec commands. The NOP command uses <c>SAHARA_EXEC_CMD_NOP</c> = 0x00.
    /// <para>会话必须处于 Sahara 命令模式：调用方需先以 <see cref="SaharaMode.Command"/> 应答 HELLO——见 <see cref="EnterCommandModeAsync(CancellationToken)"/>——才能发送 exec 命令。NOP 命令使用 <c>SAHARA_EXEC_CMD_NOP</c> = 0x00。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <exception cref="SaharaException">The target rejects the command or answers with an invalid packet.
    /// <para>目标拒绝该命令或应答包无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public Task NopAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdNop, 4096, cancellationToken);

    /// <summary>Asks the target to switch from Sahara command mode into the legacy DMSS downloader.
    /// <para>请求目标从 Sahara 命令模式切换到旧式 DMSS 下载器。</para></summary>
    /// <remarks>The command uses <c>SAHARA_EXEC_CMD_SWITCH_TO_DMSS_DLOAD</c> = 0x04. Once the target accepts, the Sahara packet sequence is over and the DMSS downloader owns the link, so <see cref="IsCommandModeReady"/> is cleared and any later exec command on this instance has to start from a new HELLO.
    /// <para>该命令使用 <c>SAHARA_EXEC_CMD_SWITCH_TO_DMSS_DLOAD</c> = 0x04。目标 一旦接受，Sahara 的包序列就此结束并由 DMSS 下载器接管链路，因此 <see cref="IsCommandModeReady"/> 会被 清除，本实例上后续的任何 exec 命令都必须从新的 HELLO 重新开始。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The switch acknowledgement payload, empty when the target sends no data.
    /// <para>切换确认负载；目标不发送数据时为空。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command or answers with an invalid packet.
    /// <para>目标拒绝该命令或应答包无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<byte[]> SwitchToDmssDloadAsync(CancellationToken cancellationToken = default)
    {
        var payload = await ExecuteDiagnosticsCommandAsync(
            EdlConstants.SaharaExecCmdSwitchToDmssDload, 4096, cancellationToken).ConfigureAwait(false);
        _commandModeReady = false;
        return payload;
    }

    /// <summary>Asks the target to switch from Sahara command mode into the streaming downloader. The stream protocol takes over the transport once this command completes.
    /// <para>请求目标从 Sahara 命令模式切换到流式下载器。该命令完成后传输层由流式协议接管。</para></summary>
    /// <remarks>The stream protocol takes over the transport once this command completes (<c>SAHARA_EXEC_CMD_SWITCH_TO_STREAM_DLOAD</c> = 0x05), so <see cref="IsCommandModeReady"/> is cleared and a later exec command on this instance has to start from a new HELLO.
    /// <para>该命令完成后流式协议接管传输（<c>SAHARA_EXEC_CMD_SWITCH_TO_STREAM_DLOAD</c> 为 0x05），因此 <see cref="IsCommandModeReady"/> 会被清除，后续 exec 命令必须从新的 HELLO 重新开始。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The switch acknowledgement payload, empty when the target sends no data.
    /// <para>切换确认负载；目标不发送数据时为空。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command or answers with an invalid packet.
    /// <para>目标拒绝该命令或应答包无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<byte[]> SwitchToStreamDloadAsync(CancellationToken cancellationToken = default)
    {
        var payload = await ExecuteDiagnosticsCommandAsync(
            EdlConstants.SaharaExecCmdSwitchToStreamDload, 4096, cancellationToken).ConfigureAwait(false);
        _commandModeReady = false;
        return payload;
    }

    /// <summary>Reads the target's debug data blob.<para>读取目标的调试数据块。</para></summary>
    /// <remarks>The command uses <c>SAHARA_EXEC_CMD_READ_DEBUG_DATA</c> = 0x06: the blob is whatever the target's debug log happens to hold, so only its self-reported length is trusted and it is bounded by 1 MiB before any byte is buffered.
    /// <para>该命令使用 <c>SAHARA_EXEC_CMD_READ_DEBUG_DATA</c> = 0x06：该数据块 的内容取决于目标的调试日志，因此只有其自报长度会被采信，并在缓冲任何字节之前以 1 MiB 为上限约束。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The debug data bytes.<para>调试数据字节。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command, answers with an invalid packet, or reports more than 1 MiB of data.
    /// <para>目标拒绝该命令、应答包无效或报告超过 1 MiB 数据。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public Task<byte[]> ReadDebugDataAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdReadDebugData, 1024 * 1024, cancellationToken);

    /// <summary>Reads the list of exec command identifiers the target firmware supports.
    /// <para>读取目标固件支持的 exec 命令号列表。</para></summary>
    /// <remarks>The command is <c>SAHARA_EXEC_CMD_GET_COMMAND_ID_LIST</c> = 0x08 with no wrapper, so the payload shape comes from <c>cmd_exec</c> itself: raw little-endian words with no per-record framing, bounded by 4 KiB before any byte is buffered.
    /// <para>该命令为 <c>SAHARA_EXEC_CMD_GET_COMMAND_ID_LIST</c> = 0x08 且没有对应封装，因此负载形状取自 <c>cmd_exec</c> 本身：不带记录分帧的小端字序列，并在缓冲任何字节之前以 4 KiB 为上限约束。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The raw command-list payload; identifiers are little-endian 32-bit words.
    /// <para>命令列表原始负载；标识符为小端 32 位字。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command, answers with an invalid packet, or reports more than 4 KiB of data.
    /// <para>目标拒绝该命令、应答包无效或报告超过 4 KiB 数据。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public Task<byte[]> GetCommandIdListAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdGetCommandIdList, 4096, cancellationToken);

    /// <summary>Reads the target's training data blob.<para>读取目标的训练数据块。</para></summary>
    /// <remarks><c>SAHARA_EXEC_CMD_GET_TRAINING_DATA</c> is 0x09 with no wrapper, so this is the same bounded <c>cmd_exec</c> exchange as the debug-data read, with the payload limited to 1 MiB before any byte is buffered.
    /// <para><c>SAHARA_EXEC_CMD_GET_TRAINING_DATA</c> 为 0x09 且没有封装，因此与读取调试数据用的是同一套有界 <c>cmd_exec</c> 交换，并在缓冲任何字节之前把负载限制为 1 MiB。</para></remarks>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The training data bytes.<para>训练数据字节。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command, answers with an invalid packet, or reports more than 1 MiB of data.
    /// <para>目标拒绝该命令、应答包无效或报告超过 1 MiB 数据。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    public Task<byte[]> GetTrainingDataAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdGetTrainingData, 1024 * 1024, cancellationToken);

    /// <summary>
    /// Generalized exec exchange: request, response with the
    /// echoed command and the reported length, then the data request and the payload read. Unlike the
    /// identity exchange the payload length is only bounded by <paramref name="maximumLength"/>, and a
    /// zero-length payload is accepted.
    /// <para>通用 exec 交换：请求、回显命令与上报长度的响应、数据请求，
    /// 最后读取负载。与身份交换不同，负载长度只受 <paramref name="maximumLength"/> 限制，且接受
    /// 零长度负载。</para>
    /// </summary>
    private async Task<byte[]> ExecuteDiagnosticsCommandAsync(uint command, int maximumLength,
        CancellationToken cancellationToken)
    {
        await WriteSaharaCommandWordAsync(SaharaCommand.Execute, command, cancellationToken).ConfigureAwait(false);
        var headerBytes = await ReadExactAsync(8, _timeoutMs, cancellationToken).ConfigureAwait(false);
        var header = BytesToStruct<SaharaPacketHeader>(headerBytes);
        if (header.Length < 16 || header.Length > 64)
        {
            throw new SaharaException("The Sahara exec response packet length is invalid.");
        }

        var body = await ReadExactAsync((int)header.Length - 8, _timeoutMs, cancellationToken).ConfigureAwait(false);
        ValidateCommand(header.Command, SaharaCommand.ExecuteResponse, header.Length, 16);
        var echoedCommand = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        if (echoedCommand != command || length > (uint)maximumLength)
        {
            throw new SaharaException("The Sahara exec response command or length is invalid.");
        }

        await WriteSaharaCommandWordAsync(SaharaCommand.ExecuteData, command, cancellationToken).ConfigureAwait(false);
        return await ReadExactAsync((int)length, _timeoutMs, cancellationToken).ConfigureAwait(false);
    }
}