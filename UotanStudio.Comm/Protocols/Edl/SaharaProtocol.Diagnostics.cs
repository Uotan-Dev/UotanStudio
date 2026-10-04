using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Diagnostic Sahara command-mode exec commands beyond identity: the reference implementations expose
/// switch-to-dmss/stream-dload, debug data, the supported-command list and training data
/// (reference <c>edlclient/Library/sahara.py</c> cmdexec_* and <c>sahara_defs.py</c> exec_cmd_t).
/// <para>身份查询之外的 Sahara 命令模式诊断 exec 命令：参考实现提供了切换 DMSS/流式下载、调试数据、
/// 支持命令列表与训练数据（参考 <c>edlclient/Library/sahara.py</c> 的 cmdexec_* 与
/// <c>sahara_defs.py</c> 的 exec_cmd_t）。</para>
/// </summary>
public sealed partial class SaharaProtocol
{
    /// <summary>
    /// Sends the command-mode no-operation exec command and confirms the target answers it.
    /// <para>发送命令模式空操作 exec 命令并确认目标应答。</para>
    /// </summary>
    /// <remarks>
    /// The session must sit in Sahara command mode: the caller answers the HELLO with
    /// <see cref="SaharaMode.Command"/> before issuing exec commands.
    /// <para>会话必须处于 Sahara 命令模式：调用方需先以 <see cref="SaharaMode.Command"/> 应答
    /// HELLO 才能发送 exec 命令。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="SaharaException">The target rejects the command or answers with an invalid packet.
    /// <para>目标拒绝该命令或应答包无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    public Task NopAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdNop, 4096, cancellationToken);

    /// <summary>
    /// Asks the target to switch from Sahara command mode into the legacy DMSS downloader.
    /// <para>请求目标从 Sahara 命令模式切换到旧式 DMSS 下载器。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The switch acknowledgement payload, empty when the target sends no data.
    /// <para>切换确认负载；目标不发送数据时为空。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command or answers with an invalid packet.
    /// <para>目标拒绝该命令或应答包无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    public Task<byte[]> SwitchToDmssDloadAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdSwitchToDmssDload, 4096, cancellationToken);

    /// <summary>
    /// Asks the target to switch from Sahara command mode into the streaming downloader. The stream
    /// protocol takes over the transport once this command completes.
    /// <para>请求目标从 Sahara 命令模式切换到流式下载器。该命令完成后传输层由流式协议接管。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The switch acknowledgement payload, empty when the target sends no data.
    /// <para>切换确认负载；目标不发送数据时为空。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command or answers with an invalid packet.
    /// <para>目标拒绝该命令或应答包无效。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    public Task<byte[]> SwitchToStreamDloadAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdSwitchToStreamDload, 4096, cancellationToken);

    /// <summary>
    /// Reads the target's debug data blob.
    /// <para>读取目标的调试数据块。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The debug data bytes. <para>调试数据字节。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command, answers with an invalid packet, or
    /// reports more than 1 MiB of data. <para>目标拒绝该命令、应答包无效或报告超过 1 MiB 数据。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    public Task<byte[]> ReadDebugDataAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdReadDebugData, 1024 * 1024, cancellationToken);

    /// <summary>
    /// Reads the list of exec command identifiers the target firmware supports.
    /// <para>读取目标固件支持的 exec 命令号列表。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The raw command-list payload; identifiers are little-endian 32-bit words.
    /// <para>命令列表原始负载；标识符为小端 32 位字。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command, answers with an invalid packet, or
    /// reports more than 4 KiB of data. <para>目标拒绝该命令、应答包无效或报告超过 4 KiB 数据。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    public Task<byte[]> GetCommandIdListAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdGetCommandIdList, 4096, cancellationToken);

    /// <summary>
    /// Reads the target's training data blob.
    /// <para>读取目标的训练数据块。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The training data bytes. <para>训练数据字节。</para></returns>
    /// <exception cref="SaharaException">The target rejects the command, answers with an invalid packet, or
    /// reports more than 1 MiB of data. <para>目标拒绝该命令、应答包无效或报告超过 1 MiB 数据。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    public Task<byte[]> GetTrainingDataAsync(CancellationToken cancellationToken = default)
        => ExecuteDiagnosticsCommandAsync(EdlConstants.SaharaExecCmdGetTrainingData, 1024 * 1024, cancellationToken);

    /// <summary>
    /// Generalized exec exchange mirroring the reference <c>cmd_exec</c>: request, response with the
    /// echoed command and the reported length, then the data request and the payload read. Unlike the
    /// identity exchange the payload length is only bounded by <paramref name="maximumLength"/>, and a
    /// zero-length payload is accepted.
    /// <para>镜像参考 <c>cmd_exec</c> 的通用 exec 交换：请求、回显命令与上报长度的响应、数据请求，
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
