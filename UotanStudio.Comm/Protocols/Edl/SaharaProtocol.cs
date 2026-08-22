using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Implements the Sahara protocol handshake for EDL (Emergency Download Mode) devices.
/// <para>实现 EDL 设备的 Sahara 协议握手。</para>
/// Protocol flow: passive HELLO read → HELLO_RESP → READ_DATA/READ_DATA64 loop → END_OF_IMAGE → DONE → DONE_RESPONSE → complete.
/// </summary>
public sealed class SaharaProtocol
{
    private readonly IUsbDeviceSession _transport;
    private readonly int _timeoutMs;
    private bool _disposed;

    /// <summary>
    /// Gets the negotiated maximum packet length.
    /// <para>获取协商的最大包长度。</para>
    /// </summary>
    public uint MaxPacketLength { get; private set; } = EdlConstants.DefaultMaxPacketLength;

    /// <summary>
    /// Gets whether the device is already running Firehose (skipped Sahara).
    /// <para>设备是否已在运行 Firehose（跳过了 Sahara）。</para>
    /// </summary>
    public bool FirehoseAlreadyRunning { get; private set; }

    /// <summary>
    /// Raised when the handshake completes.
    /// <para>握手完成时触发。</para>
    /// </summary>
    public event EventHandler<SaharaMode>? HandshakeComplete;

    /// <summary>
    /// Raised for log messages.
    /// <para>日志消息事件。</para>
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaharaProtocol"/> class.
    /// <para>初始化 <see cref="SaharaProtocol"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The USB transport session. USB 传输会话。</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
    public SaharaProtocol(IUsbDeviceSession transport, int? timeoutMs = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    }

    /// <summary>
    /// Performs the Sahara handshake, uploading the specified programmer image.
    /// <para>执行 Sahara 握手，上传指定的 programmer 镜像。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream. 可选的 programmer 镜像流。</param>
    /// <param name="imageId">The image identifier. 镜像标识符。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The negotiated Sahara mode. 协商的 Sahara 模式。</returns>
    public async Task<SaharaMode> HandshakeAsync(Stream? programmerImage, SaharaImageId imageId = SaharaImageId.Firehose, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Dictionary<uint, Stream> images = programmerImage != null
            ? new Dictionary<uint, Stream> { [(uint)imageId] = programmerImage }
            : new Dictionary<uint, Stream>();
        return await HandshakeAsync(images, cancellationToken);
    }

    /// <summary>
    /// Performs the Sahara handshake with a set of images.
    /// <para>使用一组镜像执行 Sahara 握手。</para>
    /// </summary>
    /// <param name="images">The images to upload. 要上传的镜像。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The negotiated Sahara mode. 协商的 Sahara 模式。</returns>
    public async Task<SaharaMode> HandshakeAsync(IReadOnlyDictionary<uint, Stream> images, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Log("Waiting for Sahara HELLO from device...");

        var helloProbeTimeout = Math.Min(_timeoutMs, EdlConstants.HelloProbeTimeoutMs);
        byte[] firstData;

        try
        {
            firstData = await ReadExactAsync(Marshal.SizeOf<SaharaHelloPacket>(), helloProbeTimeout, cancellationToken);
        }
        catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
        {
            if (await ProbeFirehoseNopAsync(cancellationToken))
            {
                FirehoseAlreadyRunning = true;
                Log("Device is already running Firehose; skipping Sahara handshake.");
                return SaharaMode.ImageTxComplete;
            }
            throw;
        }

        if (LooksLikeFirehoseXml(firstData))
        {
            FirehoseAlreadyRunning = true;
            Log("Device is already running Firehose; skipping Sahara handshake.");
            return SaharaMode.ImageTxComplete;
        }

        if (images.Count == 0)
            throw new SaharaException("Device is in Sahara mode but no programmer image was provided.");

        SaharaHelloPacket helloPacket = BytesToStruct<SaharaHelloPacket>(firstData);
        ValidateCommand(helloPacket.Command, SaharaCommand.Hello, helloPacket.Length, (uint)Marshal.SizeOf<SaharaHelloPacket>());

        Log($"Received HELLO: Version={helloPacket.Version}, MinVersion={helloPacket.MinVersion}, MaxPacketLength={helloPacket.MaxPacketLength}, Mode={helloPacket.Mode}");

        if (helloPacket.MinVersion > EdlConstants.SaharaVersion)
            throw new SaharaException($"Device requires min protocol version {helloPacket.MinVersion}, host supports {EdlConstants.SaharaVersion}");
        if (helloPacket.Version < EdlConstants.SaharaMinVersion)
            throw new SaharaException($"Device max protocol version {helloPacket.Version} < host min {EdlConstants.SaharaMinVersion}");

        var negotiatedVersion = Math.Min(helloPacket.Version, EdlConstants.SaharaVersion);
        var maxPacketLength = Math.Min(helloPacket.MaxPacketLength, EdlConstants.DefaultMaxPacketLength);
        MaxPacketLength = maxPacketLength;
        SaharaMode mode = helloPacket.Mode;

        Log($"Sending HELLO_RESPONSE: Version={negotiatedVersion}, MaxPacketLength={maxPacketLength}, Mode={mode}");

        var helloResponse = new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = negotiatedVersion,
            Compatible = EdlConstants.SaharaHostCompatibleVersion,
            Status = 0,
            Mode = mode
        };
        await WriteStructAsync(helloResponse, cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerData = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken);
            SaharaPacketHeader header = BytesToStruct<SaharaPacketHeader>(headerData);

            switch (header.Command)
            {
                case SaharaCommand.ReadData:
                    {
                        var fullData = await ReadRemainingPacket<SaharaReadDataPacket>(headerData, cancellationToken);
                        SaharaReadDataPacket rp = BytesToStruct<SaharaReadDataPacket>(fullData);
                        Log($"READ_DATA: ImageId={rp.ImageId}, Offset={rp.DataOffset}, Length={rp.DataLength}");
                        if (!images.TryGetValue(rp.ImageId, out Stream? img))
                            throw new SaharaException($"Unknown image id {rp.ImageId}");
                        img.Position = rp.DataOffset;
                        var buf = new byte[rp.DataLength];
                        var n = await img.ReadAsync(buf, cancellationToken);
                        await WriteDataAsync(buf.AsMemory(0, n), cancellationToken);
                        break;
                    }

                case SaharaCommand.ReadData64:
                    {
                        var full64 = await ReadRemainingPacket<SaharaReadData64Packet>(headerData, cancellationToken);
                        SaharaReadData64Packet rp64 = BytesToStruct<SaharaReadData64Packet>(full64);
                        Log($"READ_DATA64: ImageId={rp64.ImageId}, Offset=0x{rp64.DataOffset:X16}, Length=0x{rp64.DataLength:X16}");
                        if (!images.TryGetValue((uint)rp64.ImageId, out Stream? img64))
                            throw new SaharaException($"Unknown image id {rp64.ImageId}");
                        img64.Position = (long)rp64.DataOffset;
                        var buf64 = new byte[rp64.DataLength];
                        var n64 = await img64.ReadAsync(buf64, cancellationToken);
                        await WriteDataAsync(buf64.AsMemory(0, n64), cancellationToken);
                        break;
                    }

                case SaharaCommand.EndOfImage:
                    {
                        var fullEoi = await ReadRemainingPacket<SaharaEndOfImagePacket>(headerData, cancellationToken);
                        SaharaEndOfImagePacket eoi = BytesToStruct<SaharaEndOfImagePacket>(fullEoi);
                        Log($"END_OF_IMAGE: ImageId={eoi.ImageId}, Status={eoi.Status}");
                        var done = new SaharaDonePacket { Command = SaharaCommand.Done, Length = (uint)Marshal.SizeOf<SaharaDonePacket>() };
                        await WriteStructAsync(done, cancellationToken);
                        break;
                    }

                case SaharaCommand.DoneResponse:
                    {
                        var fullDone = await ReadRemainingPacket<SaharaDoneResponsePacket>(headerData, cancellationToken);
                        SaharaDoneResponsePacket dr = BytesToStruct<SaharaDoneResponsePacket>(fullDone);
                        Log($"DONE_RESPONSE: Status={dr.Status}");
                        if (dr.Status == 0 || dr.Status == 1)
                        {
                            HandshakeComplete?.Invoke(this, mode);
                            return mode;
                        }
                        throw new SaharaException($"Unexpected DONE_RESPONSE status: {dr.Status}");
                    }

                case SaharaCommand.CommandReady:
                    Log("Received COMMAND_READY.");
                    break;

                case SaharaCommand.Reset:
                    Log("Received RESET; sending RESET_RESPONSE.");
                    var rr = new SaharaResetResponsePacket { Command = SaharaCommand.ResetResponse, Length = (uint)Marshal.SizeOf<SaharaResetResponsePacket>() };
                    await WriteStructAsync(rr, cancellationToken);
                    return mode;

                default:
                    throw new SaharaException($"Unexpected Sahara command: {header.Command}");
            }
        }
    }

    /// <summary>
    /// Probes whether the device is already running Firehose by sending a NOP.
    /// <para>通过发送 NOP 探测设备是否已在运行 Firehose。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns><c>true</c> if Firehose is detected. 如果检测到 Firehose 则返回 <c>true</c>。</returns>
    private async Task<bool> ProbeFirehoseNopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nopBytes = Encoding.UTF8.GetBytes(EdlConstants.XmlHeader + EdlConstants.DataOpen + "<nop value=\"ping\"/>\n" + EdlConstants.DataClose);
            await WriteDataAsync(nopBytes, cancellationToken);
            var sb = new StringBuilder();
            var start = Environment.TickCount64;
            var budget = Math.Max(_timeoutMs, 3000);
            while ((Environment.TickCount64 - start) < budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] chunk;
                try { chunk = await ReadExactAsync(4096, _timeoutMs, cancellationToken); } catch (EdlException) { break; }
                if (chunk.Length == 0) break;
                sb.Append(Encoding.UTF8.GetString(chunk));
                var s = sb.ToString();
                if (s.Contains("<response", StringComparison.OrdinalIgnoreCase) && s.Contains("ACK", StringComparison.OrdinalIgnoreCase)) break;
            }
            var text = sb.ToString();
            return text.Contains("<response", StringComparison.OrdinalIgnoreCase) && text.Contains("ACK", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Checks if the data looks like a Firehose XML response.
    /// <para>检查数据是否看起来像 Firehose XML 响应。</para>
    /// </summary>
    /// <param name="d">The data to check. 要检查的数据。</param>
    /// <returns><c>true</c> if the data starts with &lt;?xml. 如果数据以 &lt;?xml 开头则返回 <c>true</c>。</returns>
    private static bool LooksLikeFirehoseXml(byte[] d)
    {
        return d != null && d.Length >= 5 && d[0] == (byte)'<' && d[1] == (byte)'?' && d[2] == (byte)'x' && d[3] == (byte)'m' && d[4] == (byte)'l';
    }

    /// <summary>
    /// Reads an exact number of bytes from the transport.
    /// <para>从传输中读取精确数量的字节。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. 要读取的字节数。</param>
    /// <param name="timeoutMs">The timeout in milliseconds. 超时（毫秒）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The read bytes. 读取的字节。</returns>
    /// <exception cref="EdlException">Thrown on timeout. 超时时抛出。</exception>
    private async Task<byte[]> ReadExactAsync(int length, int timeoutMs, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var total = 0;
            while (total < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = _transport.ReadInto(buf, total, length - total, timeoutMs);
                if (n == 0) throw new EdlException(EdlErrorCode.ReadPortTimeout);
                total += n;
            }
            var result = new byte[length];
            Array.Copy(buf, result, length);
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>
    /// Writes data to the transport.
    /// <para>将数据写入传输。</para>
    /// </summary>
    /// <param name="data">The data to write. 要写入的数据。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task WriteDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(buf);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var w = _transport.Write(buf, 0, data.Length, _timeoutMs);
                if (w != data.Length) throw new EdlException(EdlErrorCode.WritePortTimeout);
            }, cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>
    /// Writes a struct to the transport.
    /// <para>将结构体写入传输。</para>
    /// </summary>
    /// <param name="p">The struct to write. 要写入的结构体。</param>
    /// <param name="ct">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task WriteStructAsync<T>(T p, CancellationToken ct) where T : struct
    {
        await WriteDataAsync(StructToBytes(p), ct);
    }

    /// <summary>
    /// Reads the remaining bytes of a packet after the header.
    /// <para>读取包头之后的剩余字节。</para>
    /// </summary>
    /// <param name="hd">The header bytes. 包头字节。</param>
    /// <param name="ct">Cancellation token. 取消令牌。</param>
    /// <returns>The complete packet bytes. 完整的包字节。</returns>
    private async Task<byte[]> ReadRemainingPacket<T>(byte[] hd, CancellationToken ct) where T : struct
    {
        var sz = Marshal.SizeOf<T>();
        var fd = new byte[sz];
        Array.Copy(hd, fd, Math.Min(hd.Length, sz));
        var rem = sz - hd.Length;
        if (rem > 0)
        {
            var r = await ReadExactAsync(rem, _timeoutMs, ct);
            Array.Copy(r, 0, fd, hd.Length, Math.Min(r.Length, rem));
        }
        return fd;
    }

    /// <summary>
    /// Validates that the actual command and length match expected values.
    /// <para>验证实际命令和长度是否与预期值匹配。</para>
    /// </summary>
    /// <param name="act">The actual command. 实际命令。</param>
    /// <param name="exp">The expected command. 预期命令。</param>
    /// <param name="len">The actual length. 实际长度。</param>
    /// <param name="expLen">The expected length. 预期长度。</param>
    /// <exception cref="SaharaException">Thrown on mismatch. 不匹配时抛出。</exception>
    private static void ValidateCommand(SaharaCommand act, SaharaCommand exp, uint len, uint expLen)
    {
        if (act != exp) throw new SaharaException($"Expected {exp}, got {act}.");
        if (len != expLen) throw new SaharaException($"Invalid packet length: expected {expLen}, got {len}.");
    }

    /// <summary>
    /// Converts a byte array to a struct.
    /// <para>将字节数组转换为结构体。</para>
    /// </summary>
    /// <param name="d">The byte array. 字节数组。</param>
    /// <returns>The converted struct. 转换后的结构体。</returns>
    /// <exception cref="SaharaException">Thrown if data is too short. 数据太短时抛出。</exception>
    private static T BytesToStruct<T>(byte[] d) where T : struct
    {
        if (d.Length < Marshal.SizeOf<T>()) throw new SaharaException($"Packet too short for {typeof(T).Name}: {d.Length}.");
        return System.Runtime.InteropServices.MemoryMarshal.Read<T>(d);
    }

    /// <summary>
    /// Converts a struct to a byte array.
    /// <para>将结构体转换为字节数组。</para>
    /// </summary>
    /// <param name="s">The struct to convert. 要转换的结构体。</param>
    /// <returns>The byte array. 字节数组。</returns>
    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var b = new byte[size];
        var h = System.Runtime.InteropServices.GCHandle.Alloc(b, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { System.Runtime.InteropServices.Marshal.StructureToPtr(s, h.AddrOfPinnedObject(), false); } finally { h.Free(); }
        return b;
    }

    /// <summary>
    /// Logs a message via the <see cref="LogMessage"/> event.
    /// <para>通过 <see cref="LogMessage"/> 事件记录消息。</para>
    /// </summary>
    /// <param name="msg">The message to log. 要记录的消息。</param>
    private void Log(string msg) => LogMessage?.Invoke(this, msg);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the protocol has been disposed.
    /// <para>如果协议已释放，则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SaharaProtocol)); }

    /// <summary>
    /// Disposes the protocol.
    /// <para>释放协议。</para>
    /// </summary>
    public void Dispose() { _disposed = true; }
}

/// <summary>
/// Sahara packet header structure.
/// <para>Sahara 包头结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaPacketHeader
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}

/// <summary>
/// Sahara HELLO packet structure.
/// <para>Sahara HELLO 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaHelloPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The protocol version. <para>协议版本。</para></summary>
    public uint Version;
    /// <summary>The minimum supported version. <para>最低支持版本。</para></summary>
    public uint MinVersion;
    /// <summary>The maximum packet length. <para>最大包长度。</para></summary>
    public uint MaxPacketLength;
    /// <summary>The operating mode. <para>操作模式。</para></summary>
    public SaharaMode Mode;
}

/// <summary>
/// Sahara HELLO_RESPONSE packet structure.
/// <para>Sahara HELLO_RESPONSE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaHelloResponsePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The protocol version. <para>协议版本。</para></summary>
    public uint Version;
    /// <summary>The compatible version. <para>兼容版本。</para></summary>
    public uint Compatible;
    /// <summary>The status code. <para>状态码。</para></summary>
    public uint Status;
    /// <summary>The operating mode. <para>操作模式。</para></summary>
    public SaharaMode Mode;
}

/// <summary>
/// Sahara READ_DATA packet structure.
/// <para>Sahara READ_DATA 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaReadDataPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The image identifier. <para>镜像标识。</para></summary>
    public uint ImageId;
    /// <summary>The data offset. <para>数据偏移。</para></summary>
    public uint DataOffset;
    /// <summary>The data length. <para>数据长度。</para></summary>
    public uint DataLength;
}

/// <summary>
/// Sahara READ_DATA64 packet structure.
/// <para>Sahara READ_DATA64 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaReadData64Packet
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The image identifier (64-bit). <para>镜像标识（64 位）。</para></summary>
    public ulong ImageId;
    /// <summary>The data offset (64-bit). <para>数据偏移（64 位）。</para></summary>
    public ulong DataOffset;
    /// <summary>The data length (64-bit). <para>数据长度（64 位）。</para></summary>
    public ulong DataLength;
}

/// <summary>
/// Sahara END_OF_IMAGE packet structure.
/// <para>Sahara END_OF_IMAGE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaEndOfImagePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The image identifier. <para>镜像标识。</para></summary>
    public uint ImageId;
    /// <summary>The status code. <para>状态码。</para></summary>
    public uint Status;
}

/// <summary>
/// Sahara DONE packet structure.
/// <para>Sahara DONE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaDonePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}

/// <summary>
/// Sahara DONE_RESPONSE packet structure.
/// <para>Sahara DONE_RESPONSE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaDoneResponsePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The status code. <para>状态码。</para></summary>
    public uint Status;
}

/// <summary>
/// Sahara RESET_RESPONSE packet structure.
/// <para>Sahara RESET_RESPONSE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SaharaResetResponsePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}
