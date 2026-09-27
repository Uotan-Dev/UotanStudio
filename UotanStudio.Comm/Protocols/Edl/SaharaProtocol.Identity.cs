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
        await WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = Math.Min(hello.Version, EdlConstants.SaharaVersion),
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Mode = SaharaMode.Command
        }, token).ConfigureAwait(false);
        var ready = BytesToStruct<SaharaPacketHeader>(
            await ReadExactAsync(8, _timeoutMs, token).ConfigureAwait(false));
        ValidateCommand(ready.Command, SaharaCommand.CommandReady, ready.Length, 8);
        var serial = DecodeIdentityWord(await ExecuteIdentityCommandAsync(1, 8, token).ConfigureAwait(false));
        var hardware = DecodeIdentityWord(await ExecuteIdentityCommandAsync(2, 8, token).ConfigureAwait(false));
        var hash = await ExecuteIdentityCommandAsync(3, 4096, token).ConfigureAwait(false);
        var version = DecodeIdentityWord(await ExecuteIdentityCommandAsync(7, 8, token).ConfigureAwait(false));
        await WriteSaharaCommandWordAsync(SaharaCommand.SwitchMode, (uint)SaharaMode.ImageTxPending, token).ConfigureAwait(false);
        var imageHello = BytesToStruct<SaharaHelloPacket>(
            await ReadExactAsync(Marshal.SizeOf<SaharaHelloPacket>(), _timeoutMs, token).ConfigureAwait(false));
        ValidateHello(imageHello);
        if (imageHello.Mode != SaharaMode.ImageTxPending)
        {
            throw new SaharaException("The device did not return to Sahara image mode after the identity query.");
        }
        DeviceIdentity = new SaharaDeviceIdentity(serial, hardware, version, Convert.ToHexString(hash));
        return imageHello;
    }

    private async Task<byte[]> ExecuteIdentityCommandAsync(uint command, int maximumLength, CancellationToken token)
    {
        await WriteSaharaCommandWordAsync(SaharaCommand.Execute, command, token).ConfigureAwait(false);
        var headerBytes = await ReadExactAsync(8, _timeoutMs, token).ConfigureAwait(false);
        var header = BytesToStruct<SaharaPacketHeader>(headerBytes);
        ValidateCommand(header.Command, SaharaCommand.ExecuteResponse, header.Length, 16);
        var body = await ReadExactAsync(8, _timeoutMs, token).ConfigureAwait(false);
        var echoedCommand = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        if (echoedCommand != command || length == 0 || length > maximumLength
            || (command != 3 && length is not (4 or 8)))
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