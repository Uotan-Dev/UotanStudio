using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Implements the Sahara protocol handshake for EDL (Emergency Download Mode) devices.
/// <para>实现 EDL 设备的 Sahara 协议握手。</para>
/// Protocol flow: passive HELLO read → HELLO_RESP → READ_DATA/READ_DATA64 loop → END_OF_IMAGE → DONE → DONE_RESPONSE → complete.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SaharaProtocol"/> class.
/// <para>初始化 <see cref="SaharaProtocol"/> 类的新实例。</para>
/// </remarks>
/// <param name="transport">The device transport. 设备传输。</param>
/// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
public sealed class SaharaProtocol(ICommTransport transport, int? timeoutMs = null)
{
    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _timeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private const int ImageTransferBufferSize = 64 * 1024;
    private bool _disposed;

    /// <summary>
    /// Gets the negotiated maximum packet length.
    /// <para>获取协商的最大包长度。</para>
    /// </summary>
    public uint MaxPacketLength { get; private set; } = EdlConstants.DefaultMaxPacketLength;

    /// <summary>
    /// Gets or sets whether the QUD HELLO recovery is enabled. When the initial passive HELLO
    /// read times out (the QUD driver may have swallowed the device's first HELLO), an
    /// unsolicited 48-byte HELLO_RESPONSE is sent so the device re-sends its HELLO.
    /// <para>获取或设置是否启用 QUD HELLO 恢复。当初始被动 HELLO 读取超时（QUD 驱动可能
    /// 吞掉了设备的首个 HELLO）时，主动发送 48 字节的 HELLO_RESPONSE 让设备重发 HELLO。</para>
    /// </summary>
    public bool QudHelloRecoveryEnabled { get; set; }

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
        var images = programmerImage != null
            ? new Dictionary<uint, Stream> { [(uint)imageId] = programmerImage }
            : [];
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
            if (QudHelloRecoveryEnabled)
            {
                Log("First read timed out; the QUD driver may have consumed the HELLO. " +
                    "Sending unsolicited HELLO_RESPONSE and waiting for the device to re-send HELLO...");
                var probeHello = new SaharaHelloResponsePacket
                {
                    Command = SaharaCommand.HelloResponse,
                    Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
                    Version = EdlConstants.SaharaVersion,
                    Compatible = EdlConstants.SaharaHostCompatibleVersion,
                    Status = 0,
                    Mode = SaharaMode.ImageTxPending
                };
                await WriteStructAsync(probeHello, cancellationToken);
                try
                {
                    firstData = await ReadExactAsync(Marshal.SizeOf<SaharaHelloPacket>(), _timeoutMs, cancellationToken);
                }
                catch (EdlException recoveryEx) when (recoveryEx.ErrorCode == EdlErrorCode.ReadPortTimeout)
                {
                    if (await ProbeFirehoseNopAsync(cancellationToken))
                    {
                        FirehoseAlreadyRunning = true;
                        Log("Device is already running Firehose; skipping Sahara handshake.");
                        return SaharaMode.ImageTxComplete;
                    }
                    throw;
                }
            }
            else if (await ProbeFirehoseNopAsync(cancellationToken))
            {
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
            FirehoseAlreadyRunning = true;
            Log("Device is already running Firehose; skipping Sahara handshake.");
            return SaharaMode.ImageTxComplete;
        }

        if (images.Count == 0)
        {
            throw new SaharaException("Device is in Sahara mode but no programmer image was provided.");
        }

        var helloPacket = BytesToStruct<SaharaHelloPacket>(firstData);
        ValidateCommand(helloPacket.Command, SaharaCommand.Hello, helloPacket.Length, (uint)Marshal.SizeOf<SaharaHelloPacket>());

        Log($"Received HELLO: Version={helloPacket.Version}, MinVersion={helloPacket.MinVersion}, MaxPacketLength={helloPacket.MaxPacketLength}, Mode={helloPacket.Mode}");

        if (helloPacket.MinVersion > EdlConstants.SaharaVersion)
        {
            throw new SaharaException($"Device requires min protocol version {helloPacket.MinVersion}, host supports {EdlConstants.SaharaVersion}");
        }

        if (helloPacket.Version < EdlConstants.SaharaMinVersion)
        {
            throw new SaharaException($"Device max protocol version {helloPacket.Version} < host min {EdlConstants.SaharaMinVersion}");
        }

        var negotiatedVersion = Math.Min(helloPacket.Version, EdlConstants.SaharaVersion);
        var maxPacketLength = Math.Min(helloPacket.MaxPacketLength, EdlConstants.DefaultMaxPacketLength);
        MaxPacketLength = maxPacketLength;
        var mode = helloPacket.Mode;

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

        Log("Starting image transfer...");
        var handshakeComplete = false;

        while (!handshakeComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerData = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken);
            var header = BytesToStruct<SaharaPacketHeader>(headerData);

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
                            throw new SaharaException($"END_OF_IMAGE with non-zero status: {eoi.Status}");
                        }

                        var done = new SaharaDonePacket { Command = SaharaCommand.Done, Length = (uint)Marshal.SizeOf<SaharaDonePacket>() };
                        await WriteStructAsync(done, cancellationToken);
                        Log("Sent DONE, image transfer complete.");
                        break;
                    }

                case SaharaCommand.DoneResponse:
                    {
                        var fullDone = await ReadRemainingPacket<SaharaDoneResponsePacket>(headerData, cancellationToken);
                        var dr = BytesToStruct<SaharaDoneResponsePacket>(fullDone);
                        Log($"DONE_RESPONSE: Status={dr.Status}");
                        if (dr.Status == 1)
                        {
                            handshakeComplete = true;
                            break;
                        }
                        if (dr.Status == 0)
                        {
                            if (HasDonePendingQuirk(images))
                            {
                                Log("DONE_RESPONSE status=0 (PENDING) with single firehose image; " +
                                    "treating as complete (MSM8916 quirk).");
                                handshakeComplete = true;
                                break;
                            }
                            break;
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
                    handshakeComplete = true;
                    break;

                case SaharaCommand.ResetResponse:
                    Log("Received RESET_RESPONSE.");
                    handshakeComplete = true;
                    break;
                case SaharaCommand.Hello:
                    break;
                case SaharaCommand.HelloResponse:
                    break;
                case SaharaCommand.Done:
                    break;
                case SaharaCommand.MemoryDebug:
                    break;
                case SaharaCommand.MemoryRead:
                    break;
                case SaharaCommand.SwitchMode:
                    break;
                case SaharaCommand.Execute:
                    break;
                case SaharaCommand.ExecuteResponse:
                    break;
                case SaharaCommand.ExecuteData:
                    break;
                case SaharaCommand.MemoryDebug64:
                    break;
                case SaharaCommand.MemoryRead64:
                    break;
                default:
                    throw new SaharaException($"Unexpected Sahara command: {header.Command}");
            }
        }

        try
        {
            var trailing = await ReadExactAsync(Marshal.SizeOf<SaharaPacketHeader>(), EdlConstants.SaharaTrailingPacketTimeoutMs, cancellationToken);
            var trailingHeader = BytesToStruct<SaharaPacketHeader>(trailing);
            Log($"Received final packet after handshake: {trailingHeader.Command}.");
        }
        catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
        {
            Log("No final packet after handshake (timeout); handshake considered complete.");
        }

        HandshakeComplete?.Invoke(this, mode);
        return mode;
    }

    /// <summary>
    /// Validates that a READ_DATA/READ_DATA64 offset and length fall within the image length.
    /// <para>验证 READ_DATA/READ_DATA64 的偏移与长度是否落在镜像长度范围内。</para>
    /// </summary>
    /// <param name="offset">The requested data offset. 请求的数据偏移。</param>
    /// <param name="length">The requested data length. 请求的数据长度。</param>
    /// <param name="imageLength">The total image length. 镜像总长度。</param>
    /// <param name="command">The command name (for diagnostics). 命令名（用于诊断）。</param>
    private static void ValidateReadRange(long offset, long length, long imageLength, string command)
    {
        if (offset < 0 || length < 0 || imageLength < 0 || offset > imageLength || length > imageLength - offset)
        {
            throw new SaharaException($"{command} requests invalid range: offset={offset}, length={length}, image length={imageLength}");
        }
    }

    private async Task TransferImageRangeAsync(Stream image, long length, string command, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ImageTransferBufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                var total = 0;
                while (total < count)
                {
                    var read = await image.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new SaharaException($"{command} image stream ended before the requested {length} bytes were read.");
                    }
                    total += read;
                }

                var written = await _transport.WriteAsync(buffer, 0, count, _timeoutMs, cancellationToken).ConfigureAwait(false);
                if (written != count)
                {
                    throw new EdlException(EdlErrorCode.WritePortTimeout);
                }
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Returns whether the images dictionary holds exactly one Firehose image (MSM8916
    /// DONE_RESPONSE status=0 quirk).
    /// <para>判断镜像字典是否仅含单个 Firehose 镜像（MSM8916 DONE_RESPONSE status=0 特性）。</para>
    /// </summary>
    /// <param name="images">The images dictionary. 镜像字典。</param>
    private static bool HasDonePendingQuirk(IReadOnlyDictionary<uint, Stream> images) => images.Count == 1 && images.ContainsKey((uint)SaharaImageId.Firehose);

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
            var budget = Math.Max(_timeoutMs, EdlConstants.ProbeReadBudgetMs);
            while ((Environment.TickCount64 - start) < budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = await ReadChunkAsync(EdlConstants.ProbeReadChunkBytes, _timeoutMs, cancellationToken);
                if (chunk.Length == 0)
                {
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(chunk));
                var s = sb.ToString();
                if (s.Contains("<response", StringComparison.OrdinalIgnoreCase) && s.Contains("ACK", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
            var text = sb.ToString();
            return text.Contains("<response", StringComparison.OrdinalIgnoreCase) && text.Contains("ACK", StringComparison.OrdinalIgnoreCase);
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
    /// Checks if the data looks like a Firehose XML response.
    /// <para>检查数据是否看起来像 Firehose XML 响应。</para>
    /// </summary>
    /// <param name="d">The data to check. 要检查的数据。</param>
    /// <returns><c>true</c> if the data starts with &lt;?xml. 如果数据以 &lt;?xml 开头则返回 <c>true</c>。</returns>
    private static bool LooksLikeFirehoseXml(byte[] d) => d != null && d.Length >= 5 && d[0] == (byte)'<' && d[1] == (byte)'?' && d[2] == (byte)'x' && d[3] == (byte)'m' && d[4] == (byte)'l';

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes, returning only the bytes actually received.
    /// <para>读取最多 <paramref name="length"/> 个字节，仅返回实际收到的字节。</para>
    /// </summary>
    /// <param name="length">The maximum number of bytes to read. 最多读取的字节数。</param>
    /// <param name="timeoutMs">The timeout in milliseconds. 超时（毫秒）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The bytes actually read. 实际读取的字节。</returns>
    private async Task<byte[]> ReadChunkAsync(int length, int timeoutMs, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var n = await _transport.ReadAsync(buffer, 0, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (n == 0)
        {
            return [];
        }

        if (n == length)
        {
            return buffer;
        }

        var result = new byte[n];
        Array.Copy(buffer, result, n);
        return result;
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
                var n = await _transport.ReadAsync(buf, total, length - total, timeoutMs, cancellationToken);
                if (n == 0)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout);
                }

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
            var w = await _transport.WriteAsync(buf, 0, data.Length, _timeoutMs, cancellationToken);
            if (w != data.Length)
            {
                throw new EdlException(EdlErrorCode.WritePortTimeout);
            }
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
    private async Task WriteStructAsync<T>(T p, CancellationToken ct) where T : struct => await WriteDataAsync(StructToBytes(p), ct);

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
        if (act != exp)
        {
            throw new SaharaException($"Expected {exp}, got {act}.");
        }

        if (len != expLen)
        {
            throw new SaharaException($"Invalid packet length: expected {expLen}, got {len}.");
        }
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
        return d.Length < Marshal.SizeOf<T>()
            ? throw new SaharaException($"Packet too short for {typeof(T).Name}: {d.Length}.")
            : MemoryMarshal.Read<T>(d);
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
        var h = GCHandle.Alloc(b, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(s, h.AddrOfPinnedObject(), false); } finally { h.Free(); }
        return b;
    }

    /// <summary>
    /// Logs a Message via the <see cref="LogMessage"/> event.
    /// <para>通过 <see cref="LogMessage"/> 事件记录消息。</para>
    /// </summary>
    /// <param name="msg">The Message to log. 要记录的消息。</param>
    private void Log(string msg) => LogMessage?.Invoke(this, msg);

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the protocol has been disposed.
    /// <para>如果协议已释放，则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SaharaProtocol));
        }
    }

    /// <summary>
    /// Disposes the protocol.
    /// <para>释放协议。</para>
    /// </summary>
    public void Dispose() => _disposed = true;
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
/// Sahara HELLO packet structure (48 bytes: header + version fields + 6 reserved uints).
/// <para>Sahara HELLO 包结构（48 字节：包头 + 版本字段 + 6 个保留 uint）。</para>
/// Devices send Length=0x30; the trailing reserved words must be part of the struct or the
/// extra 24 bytes stay in the pipe and desynchronize every following packet.
/// <para>设备发送的 Length=0x30；保留字必须包含在结构体中，否则多出的 24 字节会留在管道
/// 中并使后续所有包失步。</para>
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
    /// <summary>Reserved word 0. <para>保留字 0。</para></summary>
    public uint Reserved0;
    /// <summary>Reserved word 1. <para>保留字 1。</para></summary>
    public uint Reserved1;
    /// <summary>Reserved word 2. <para>保留字 2。</para></summary>
    public uint Reserved2;
    /// <summary>Reserved word 3. <para>保留字 3。</para></summary>
    public uint Reserved3;
    /// <summary>Reserved word 4. <para>保留字 4。</para></summary>
    public uint Reserved4;
    /// <summary>Reserved word 5. <para>保留字 5。</para></summary>
    public uint Reserved5;
}

/// <summary>
/// Sahara HELLO_RESPONSE packet structure (48 bytes: header + response fields + 6 reserved uints).
/// <para>Sahara HELLO_RESPONSE 包结构（48 字节：包头 + 响应字段 + 6 个保留 uint）。</para>
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
    /// <summary>Reserved word 0. <para>保留字 0。</para></summary>
    public uint Reserved0;
    /// <summary>Reserved word 1. <para>保留字 1。</para></summary>
    public uint Reserved1;
    /// <summary>Reserved word 2. <para>保留字 2。</para></summary>
    public uint Reserved2;
    /// <summary>Reserved word 3. <para>保留字 3。</para></summary>
    public uint Reserved3;
    /// <summary>Reserved word 4. <para>保留字 4。</para></summary>
    public uint Reserved4;
    /// <summary>Reserved word 5. <para>保留字 5。</para></summary>
    public uint Reserved5;
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