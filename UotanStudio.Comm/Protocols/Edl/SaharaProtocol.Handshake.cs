using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class SaharaProtocol
{
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
                    MinVersion = EdlConstants.SaharaHostCompatibleVersion,
                    MaxCommandLength = 0,
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

        Log($"Received HELLO: Version={helloPacket.Version}, MinVersion={helloPacket.MinVersion}, MaxCommandLength={helloPacket.MaxCommandLength}, Mode={helloPacket.Mode}");

        if (helloPacket.MinVersion > EdlConstants.SaharaVersion)
        {
            throw new SaharaException($"Device requires min protocol version {helloPacket.MinVersion}, host supports {EdlConstants.SaharaVersion}");
        }

        if (helloPacket.Version < EdlConstants.SaharaMinVersion)
        {
            throw new SaharaException($"Device max protocol version {helloPacket.Version} < host min {EdlConstants.SaharaMinVersion}");
        }

        var negotiatedVersion = Math.Min(helloPacket.Version, EdlConstants.SaharaVersion);
        // Sahara field 5 of HELLO is max_cmd_len (typically 3), not a USB packet size; image
        // transfers are chunked independently, so MaxPacketLength keeps its transfer default.
        var mode = helloPacket.Mode;

        Log($"Sending HELLO_RESPONSE: Version={negotiatedVersion}, Mode={mode}");

        var helloResponse = new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = negotiatedVersion,
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            MaxCommandLength = 0,
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
                            throw new SaharaException($"END_OF_IMAGE with non-zero status: {eoi.Status} ({DescribeSaharaStatus(eoi.Status)})");
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
                        // Sahara spec: status 0 is SUCCESS; some loaders report 1. Both mean
                        // the image transfer is complete (bkerler/edl ignores the field).
                        if (dr.Status is 0 or 1)
                        {
                            handshakeComplete = true;
                            break;
                        }
                        throw new SaharaException($"Unexpected DONE_RESPONSE status: {dr.Status} ({DescribeSaharaStatus(dr.Status)})");
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
    /// Returns whether the images dictionary holds exactly one Firehose image (MSM8916
    /// DONE_RESPONSE status=0 quirk).
    /// <para>判断镜像字典是否仅含单个 Firehose 镜像（MSM8916 DONE_RESPONSE status=0 特性）。</para>
    /// </summary>
    /// <param name="images">The images dictionary. 镜像字典。</param>
    private static bool HasDonePendingQuirk(IReadOnlyDictionary<uint, Stream> images) => images.Count == 1 && images.ContainsKey((uint)SaharaImageId.Firehose);

    /// <summary>
    /// Maps common Sahara NAK status codes to descriptions (bkerler/edl sahara_defs.py).
    /// <para>将常见 Sahara NAK 状态码映射为描述（bkerler/edl 的 sahara_defs.py）。</para>
    /// </summary>
    private static string DescribeSaharaStatus(uint status) => status switch
    {
        0x01 => "invalid command received in current state",
        0x02 => "protocol mismatch between host and target",
        0x03 => "invalid target protocol version",
        0x04 => "invalid host protocol version",
        0x05 => "invalid packet size",
        0x06 => "unexpected packet id",
        0x07 => "invalid image received",
        0x08 => "invalid image transmission status",
        0x09 => "invalid RAM address",
        0x0A => "invalid data offset",
        0x0B => "invalid data length",
        0x0C => "invalid image id",
        _ => "unmapped Sahara status"
    };

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
}