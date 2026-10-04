using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class SaharaProtocol
{
    /// <summary>
    /// Gets or sets whether the handshake must query legacy command-mode identity before image upload.
    /// Firehose-only connections cannot satisfy this requirement.
    /// <para>获取或设置握手是否必须在上传镜像前查询传统命令模式身份；仅 Firehose 连接无法满足此要求。</para>
    /// </summary>
    /// <value>False by default; true requires all identity commands and the return to image mode to succeed.
    /// <para>默认为 false；为 true 时要求全部身份命令及返回镜像模式均成功。</para></value>
    public bool QueryIdentityBeforeUpload { get; set; }

    /// <summary>Gets the completed command-mode identity query, if requested.
    /// <para>获取已完成的命令模式身份查询结果（若已请求）。</para></summary>
    /// <value>The complete report, or null when no complete query is available.
    /// <para>完整报告；无完整查询结果时为 null。</para></value>
    public SaharaDeviceIdentity? DeviceIdentity { get; private set; }

    private async Task<SaharaHelloPacket> QueryIdentityOwnedAsync(SaharaHelloPacket hello, CancellationToken token)
    {
        ValidateHello(hello);
        if (hello.Mode is not (SaharaMode.ImageTxPending or SaharaMode.Command))
        {
            throw new SaharaException("Sahara identity requires an image or command-mode HELLO.");
        }
        NegotiatedProtocolVersion = Math.Min(hello.Version, EdlConstants.SaharaHostMaxProtocolVersion);
        await WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = NegotiatedProtocolVersion.Value,
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Mode = SaharaMode.Command
        }, token).ConfigureAwait(false);
        var ready = BytesToStruct<SaharaPacketHeader>(
            await ReadExactAsync(8, _timeoutMs, token).ConfigureAwait(false));
        ValidateCommand(ready.Command, SaharaCommand.CommandReady, ready.Length, 8);
        var serial = DecodeIdentityWord(await ExecuteIdentityCommandAsync(1, 8, token).ConfigureAwait(false));

        // qdl sahara.c (sahara_command_info) and bkerler/edl cmd_info: pre-v3 targets answer
        // MSM_HW_ID_READ (0x02), v3 targets no longer do and expose the same fields through
        // READ_CHIP_ID_V3 (0x0A).
        ulong hardware;
        uint chipIdentifierV3;
        if (NegotiatedProtocolVersion >= 3)
        {
            (hardware, chipIdentifierV3) = await ReadChipIdentifierV3Async(token).ConfigureAwait(false);
        }
        else
        {
            hardware = DecodeIdentityWord(await ExecuteIdentityCommandAsync(2, 8, token).ConfigureAwait(false));
            chipIdentifierV3 = 0;
        }

        // bkerler/edl cmd_info: v3 targets may refuse the OEM PK hash and SBL version reads
        // ("V3 restriction"), so on v3 they stay best-effort while pre-v3 remains strict.
        var hashBytes = await ExecuteOptionalIdentityCommandAsync(
            NegotiatedProtocolVersion >= 3, 3, 4096, token).ConfigureAwait(false);
        var version = await ExecuteOptionalIdentityCommandAsync(
            NegotiatedProtocolVersion >= 3, 7, 8, token).ConfigureAwait(false);
        await WriteSaharaCommandWordAsync(SaharaCommand.SwitchMode, (uint)SaharaMode.ImageTxPending, token).ConfigureAwait(false);
        var imageHello = BytesToStruct<SaharaHelloPacket>(
            await ReadExactAsync(Marshal.SizeOf<SaharaHelloPacket>(), _timeoutMs, token).ConfigureAwait(false));
        ValidateHello(imageHello);
        if (imageHello.Mode != SaharaMode.ImageTxPending)
        {
            throw new SaharaException("The device did not return to Sahara image mode after the identity query.");
        }
        DeviceIdentity = new SaharaDeviceIdentity(
            serial,
            hardware,
            version.Length == 0 ? 0 : DecodeIdentityWord(version),
            Convert.ToHexString(hashBytes),
            NegotiatedProtocolVersion,
            chipIdentifierV3);
        return imageHello;
    }

    /// <summary>
    /// Reads the version 3 chip identifier (SAHARA_EXEC_CMD_READ_CHIP_ID_V3, 0x0A) and composes
    /// the legacy-style hardware word from it.
    /// <para>读取版本 3 芯片标识（SAHARA_EXEC_CMD_READ_CHIP_ID_V3，0x0A），并由其组合传统格式的硬件字。</para>
    /// </summary>
    /// <remarks>
    /// Layout transcribed from linux-msm/qdl sahara.c and bkerler/edl cmdexec_get_chip_id_v3:
    /// bytes 36-39 carry the MSM_ID, 40-41 the OEM_ID, 42-43 the MODEL_ID, with an alternate
    /// OEM_ID slot at 44-45 when the primary slot reads zero.
    /// <para>布局转录自 linux-msm/qdl 的 sahara.c 与 bkerler/edl 的 cmdexec_get_chip_id_v3：
    /// 第 36-39 字节为 MSM_ID，40-41 为 OEM_ID，42-43 为 MODEL_ID；主槽读到零时改用 44-45
    /// 的备用 OEM_ID 槽位。</para>
    /// </remarks>
    /// <param name="token">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The composed hardware word and the raw 32-bit chip identifier.
    /// <para>组合后的硬件字与原始 32 位芯片标识。</para></returns>
    private async Task<(ulong Hardware, uint ChipIdentifier)> ReadChipIdentifierV3Async(CancellationToken token)
    {
        var payload = await ExecuteIdentityCommandAsync(EdlConstants.SaharaExecCmdReadChipIdV3, 64, token).ConfigureAwait(false);
        var chipIdentifier = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var msmId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(36));
        var oemId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(40));
        var modelId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(42));
        if (oemId == 0 && payload.Length >= 46)
        {
            oemId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(44));
        }
        var hardware = ((ulong)msmId << 32) | ((ulong)oemId << 16) | modelId;
        return (hardware, chipIdentifier);
    }

    /// <summary>
    /// Runs an identity command that the caller permits to fail on some targets. Strict runs
    /// rethrow exactly like <see cref="ExecuteIdentityCommandAsync"/>.
    /// <para>执行调用方允许在某些目标上失败的身份命令。严格模式与
    /// <see cref="ExecuteIdentityCommandAsync"/> 一样原样抛出。</para>
    /// </summary>
    /// <param name="optional">Whether a rejection or protocol error yields an empty result.
    /// <para>为 <see langword="true"/> 时拒绝或协议错误返回空结果。</para></param>
    /// <param name="command">The command-mode client command. <para>命令模式客户端命令。</para></param>
    /// <param name="maximumLength">The upper bound accepted for the response.
    /// <para>响应接受的最大长度。</para></param>
    /// <param name="token">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The response bytes, empty when optional and unavailable.
    /// <para>响应字节；可选且不可用时为空。</para></returns>
    private async Task<byte[]> ExecuteOptionalIdentityCommandAsync(bool optional, uint command, int maximumLength,
        CancellationToken token)
    {
        if (!optional)
        {
            return await ExecuteIdentityCommandAsync(command, maximumLength, token).ConfigureAwait(false);
        }
        try
        {
            return await ExecuteIdentityCommandAsync(command, maximumLength, token).ConfigureAwait(false);
        }
        catch (SaharaException)
        {
            return [];
        }
    }

    private async Task<byte[]> ExecuteIdentityCommandAsync(uint command, int maximumLength, CancellationToken token)
    {
        await WriteSaharaCommandWordAsync(SaharaCommand.Execute, command, token).ConfigureAwait(false);
        var headerBytes = await ReadExactAsync(8, _timeoutMs, token).ConfigureAwait(false);
        var header = BytesToStruct<SaharaPacketHeader>(headerBytes);
        if (header.Length < 16 || header.Length > 64)
        {
            throw new SaharaException("The Sahara identity response packet length is invalid.");
        }

        // The full response packet is drained before the command is validated so that a
        // refused request (best-effort v3 reads) leaves the stream aligned for recovery.
        var body = await ReadExactAsync((int)header.Length - 8, _timeoutMs, token).ConfigureAwait(false);
        ValidateCommand(header.Command, SaharaCommand.ExecuteResponse, header.Length, 16);
        var echoedCommand = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        if (echoedCommand != command || length == 0 || length > maximumLength
            || (command != 3 && command != EdlConstants.SaharaExecCmdReadChipIdV3 && length is not (4 or 8))
            || (command == EdlConstants.SaharaExecCmdReadChipIdV3 && length < 44))
        {
            throw new SaharaException("The Sahara identity response command or length is invalid.");
        }
        await WriteSaharaCommandWordAsync(SaharaCommand.ExecuteData, command, token).ConfigureAwait(false);
        return await ReadExactAsync((int)length, _timeoutMs, token).ConfigureAwait(false);
    }

    private Task WriteSaharaCommandWordAsync(SaharaCommand command, uint value, CancellationToken token)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)command);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        return WriteDataAsync(bytes, token);
    }

    private static ulong DecodeIdentityWord(byte[] bytes) => bytes.Length == 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt64LittleEndian(bytes);

    private void RequireIdentityNotRequested()
    {
        if (QueryIdentityBeforeUpload)
        {
            throw new SaharaException("A Firehose-only connection cannot provide the required Sahara device identity.");
        }
    }
}