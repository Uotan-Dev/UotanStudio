using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Usbmuxd;

/// <summary>
/// Client for the host usbmuxd daemon: lists devices, manages pair records and opens raw TCP-like
/// streams to device ports. Uses the plist protocol (version 1) and falls back to the legacy binary
/// protocol (version 0) when the daemon rejects it. Every operation opens its own daemon connection
/// and a successful CONNECT turns that connection into the device stream.
/// <para>主机 usbmuxd 守护进程客户端：列出设备、管理配对记录并打开到设备端口的原始 TCP 流。
/// 使用 plist 协议（version 1），守护进程拒绝时回退旧版二进制协议（version 0）。每个操作各自
/// 打开一条守护进程连接，CONNECT 成功后该连接即成为设备流。</para>
/// </summary>
public sealed class UsbmuxdClient : IDisposable
{
    private const string ClientVersionString = "UotanStudio-1";
    private const string ProgramName = "UotanStudio";

    private readonly UsbmuxdClientOptions _options;
    private bool _usePlist = true;
    private bool _disposed;

    /// <summary>
    /// Creates a client bound to the supplied endpoint options.
    /// <para>按给定端点配置创建客户端。</para>
    /// </summary>
    /// <param name="options">Endpoint options; platform defaults apply when omitted. <para>端点配置；缺省时使用平台默认。</para></param>
    public UsbmuxdClient(UsbmuxdClientOptions? options = null) =>
        _options = options ?? UsbmuxdClientOptions.Default;

    /// <summary>
    /// Lists every device the daemon currently knows.
    /// <para>列出守护进程当前已知的全部设备。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The connected devices. <para>已连接设备列表。</para></returns>
    /// <exception cref="UsbmuxdException">The daemon is unreachable or rejected the request. <para>守护进程不可达或拒绝请求时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public IReadOnlyList<UsbmuxdDevice> ListDevices(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_usePlist)
        {
            try
            {
                return ListDevicesPlist(cancellationToken);
            }
            catch (UsbmuxdException exception) when (exception.ResultCode == AppleConstants.UsbmuxdResultBadVersion)
            {
                _usePlist = false;
            }
        }

        return ListDevicesLegacy(cancellationToken);
    }

    /// <summary>
    /// Reads the daemon BUID (system unique identifier used when validating pair records).
    /// <para>读取守护进程 BUID（校验配对记录所用的系统唯一标识）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The BUID string. <para>BUID 字符串。</para></returns>
    /// <exception cref="UsbmuxdException">The daemon is unreachable or rejected the request. <para>守护进程不可达或拒绝请求时抛出。</para></exception>
    public string ReadBuid(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var body = RequestPlist("ReadBuid", static builder => builder, cancellationToken);
        if (body.TryGetValue("BUID", out var value) && value is string buid)
        {
            return buid;
        }

        throw new UsbmuxdException("The usbmuxd ReadBuid response does not carry a BUID.");
    }

    /// <summary>
    /// Reads the pair record stored by the daemon for a device.
    /// <para>读取守护进程为某设备保存的配对记录。</para>
    /// </summary>
    /// <param name="pairRecordId">The pair record identifier, which is the device UDID. <para>配对记录标识，即设备 UDID。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The serialized pair record plist, or <see langword="null"/> when no record exists. <para>序列化的配对记录属性列表；无记录时为 <see langword="null"/>。</para></returns>
    /// <exception cref="UsbmuxdException">The daemon is unreachable or rejected the request. <para>守护进程不可达或拒绝请求时抛出。</para></exception>
    public byte[]? ReadPairRecord(string pairRecordId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var body = RequestPlist("ReadPairRecord", builder =>
        {
            builder["PairRecordID"] = pairRecordId;
            return builder;
        }, cancellationToken);
        if (body.TryGetValue("PairRecordData", out var value) && value is byte[] data)
        {
            return data;
        }

        return null;
    }

    /// <summary>
    /// Stores a pair record in the daemon for a device.
    /// <para>在守护进程中为设备保存配对记录。</para>
    /// </summary>
    /// <param name="pairRecordId">The pair record identifier, which is the device UDID. <para>配对记录标识，即设备 UDID。</para></param>
    /// <param name="deviceId">The usbmuxd device identifier. <para>usbmuxd 设备标识。</para></param>
    /// <param name="pairRecordPlist">The serialized pair record plist. <para>序列化的配对记录属性列表。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <exception cref="UsbmuxdException">The daemon is unreachable or rejected the request. <para>守护进程不可达或拒绝请求时抛出。</para></exception>
    public void SavePairRecord(string pairRecordId, int deviceId, byte[] pairRecordPlist,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequestPlist("SavePairRecord", builder =>
        {
            builder["PairRecordID"] = pairRecordId;
            builder["DeviceID"] = (long)deviceId;
            builder["PairRecordData"] = pairRecordPlist;
            return builder;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes the pair record stored by the daemon for a device.
    /// <para>删除守护进程为设备保存的配对记录。</para>
    /// </summary>
    /// <param name="pairRecordId">The pair record identifier, which is the device UDID. <para>配对记录标识，即设备 UDID。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <exception cref="UsbmuxdException">The daemon is unreachable or rejected the request. <para>守护进程不可达或拒绝请求时抛出。</para></exception>
    public void DeletePairRecord(string pairRecordId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequestPlist("DeletePairRecord", builder =>
        {
            builder["PairRecordID"] = pairRecordId;
            return builder;
        }, cancellationToken);
    }

    /// <summary>
    /// Opens a raw byte stream to a device port over the multiplexer.
    /// <para>经复用器打开到设备端口的原始字节流。</para>
    /// </summary>
    /// <param name="serial">The device serial (UDID); resolved against the current device list. <para>设备序列号（UDID）；按当前设备列表解析。</para></param>
    /// <param name="port">The device TCP port. <para>设备 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The device byte stream. <para>设备字节流。</para></returns>
    /// <exception cref="UsbmuxdException">The device is unknown, the port was refused or the daemon is unreachable. <para>设备未知、端口被拒或守护进程不可达时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public Stream Connect(string serial, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var device in ListDevices(cancellationToken))
        {
            if (NormalizedSerial(device.SerialNumber) == NormalizedSerial(serial))
            {
                return Connect(device.DeviceId, port, cancellationToken);
            }
        }

        throw new UsbmuxdException($"The usbmuxd daemon does not know a device with serial {serial}.");
    }

    /// <summary>
    /// Opens a raw byte stream to a device port over the multiplexer.
    /// <para>经复用器打开到设备端口的原始字节流。</para>
    /// </summary>
    /// <param name="deviceId">The usbmuxd device identifier from the current device list. <para>当前设备列表中的 usbmuxd 设备标识。</para></param>
    /// <param name="port">The device TCP port. <para>设备 TCP 端口。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The device byte stream. <para>设备字节流。</para></returns>
    /// <exception cref="UsbmuxdException">The device is unknown, the port was refused or the daemon is unreachable. <para>设备未知、端口被拒或守护进程不可达时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public Stream Connect(int deviceId, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (port is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The device port must fit in 16 bits.");
        }

        var wirePort = SwapU16((ushort)port);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var connection = UsbmuxdConnection.Open(_options, cancellationToken);
            try
            {
                uint result;
                if (_usePlist)
                {
                    connection.WritePlistMessage(1, new Dictionary<string, object>
                    {
                        ["MessageType"] = "Connect",
                        ["ProgName"] = ProgramName,
                        ["ClientVersionString"] = ClientVersionString,
                        ["DeviceID"] = (long)deviceId,
                        ["PortNumber"] = (long)wirePort,
                    }, cancellationToken);
                    var message = connection.ReadMessage(cancellationToken);
                    if (message.MessageType == AppleConstants.UsbmuxdMessageResult)
                    {
                        result = BinaryPrimitives.ReadUInt32LittleEndian(message.Payload);
                    }
                    else
                    {
                        var body = UsbmuxdConnection.ParsePlistPayload(message);
                        result = ReadResultCode(body);
                    }

                    if (result == AppleConstants.UsbmuxdResultBadVersion)
                    {
                        _usePlist = false;
                        connection.Dispose();
                        continue;
                    }
                }
                else
                {
                    var payload = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)deviceId);
                    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), wirePort);
                    connection.WriteLegacyMessage(1, AppleConstants.UsbmuxdMessageConnect, payload, cancellationToken);
                    var message = connection.ReadMessage(cancellationToken);
                    if (message.MessageType != AppleConstants.UsbmuxdMessageResult ||
                        message.Payload.Length < 4)
                    {
                        throw new UsbmuxdException("The usbmuxd daemon returned a malformed connect result.");
                    }

                    result = BinaryPrimitives.ReadUInt32LittleEndian(message.Payload);
                }

                if (result != AppleConstants.UsbmuxdResultOk)
                {
                    throw new UsbmuxdException(
                        $"The usbmuxd daemon refused the connection to device {deviceId} port {port}.", result);
                }

                return connection.DetachStream();
            }
            finally
            {
                connection.Dispose();
            }
        }

        throw new UsbmuxdException("The usbmuxd daemon rejected both the plist and the legacy protocol.");
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true;

    private IReadOnlyList<UsbmuxdDevice> ListDevicesPlist(CancellationToken cancellationToken)
    {
        var body = RequestPlist("ListDevices", static builder => builder, cancellationToken);
        if (!body.TryGetValue("DeviceList", out var value) || value is not List<object> list)
        {
            throw new UsbmuxdException("The usbmuxd ListDevices response does not carry a device list.");
        }

        var devices = new List<UsbmuxdDevice>(list.Count);
        foreach (var entry in list)
        {
            if (entry is not Dictionary<string, object> record ||
                !record.TryGetValue("DeviceID", out var idValue) ||
                !record.TryGetValue("Properties", out var propertiesValue) ||
                propertiesValue is not Dictionary<string, object> properties)
            {
                continue;
            }

            var serial = properties.TryGetValue("SerialNumber", out var serialValue) &&
                serialValue is string serialText ? serialText : string.Empty;
            var connectionType = properties.TryGetValue("ConnectionType", out var typeValue) &&
                typeValue is string typeText ? typeText : UsbmuxdDevice.ConnectionTypeUsb;
            var productId = properties.TryGetValue("ProductID", out var productValue) &&
                productValue is long product ? (ushort)product : (ushort)0;
            var locationId = properties.TryGetValue("LocationID", out var locationValue) &&
                locationValue is long location ? (int)location : 0;
            devices.Add(new UsbmuxdDevice((int)(long)idValue, serial, connectionType, productId, locationId));
        }

        return devices;
    }

    private IReadOnlyList<UsbmuxdDevice> ListDevicesLegacy(CancellationToken cancellationToken)
    {
        using var connection = UsbmuxdConnection.Open(_options, cancellationToken);
        connection.WriteLegacyMessage(1, AppleConstants.UsbmuxdMessageListen, null, cancellationToken);
        var devices = new List<UsbmuxdDevice>();
        var resultReceived = false;
        while (true)
        {
            UsbmuxdMessage message;
            try
            {
                message = connection.ReadMessage(cancellationToken);
            }
            catch (UsbmuxdException exception) when (resultReceived && devices.Count == 0 &&
                exception.Message.Contains("timed out", StringComparison.Ordinal))
            {
                return devices;
            }

            if (message.MessageType == AppleConstants.UsbmuxdMessageResult)
            {
                var result = message.Payload.Length >= 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(message.Payload)
                    : AppleConstants.UsbmuxdResultBadCommand;
                if (result != AppleConstants.UsbmuxdResultOk)
                {
                    throw new UsbmuxdException("The usbmuxd daemon rejected the listen request.", result);
                }

                resultReceived = true;
                if (devices.Count > 0)
                {
                    return devices;
                }
            }
            else if (message.MessageType == AppleConstants.UsbmuxdMessageDeviceAdd &&
                     message.Payload.Length >= 268)
            {
                devices.Add(ParseLegacyDeviceRecord(message.Payload));
            }
            else if (message.MessageType == AppleConstants.UsbmuxdMessageDeviceRemove)
            {
                var removed = message.Payload.Length >= 4
                    ? (int)BinaryPrimitives.ReadUInt32LittleEndian(message.Payload)
                    : -1;
                devices.RemoveAll(device => device.DeviceId == removed);
            }
        }
    }

    private Dictionary<string, object> RequestPlist(string messageType,
        Func<Dictionary<string, object>, Dictionary<string, object>> extend, CancellationToken cancellationToken)
    {
        using var connection = UsbmuxdConnection.Open(_options, cancellationToken);
        var builder = new Dictionary<string, object>
        {
            ["MessageType"] = messageType,
            ["ProgName"] = ProgramName,
            ["ClientVersionString"] = ClientVersionString,
        };
        connection.WritePlistMessage(1, extend(builder), cancellationToken);
        var message = connection.ReadMessage(cancellationToken);
        var body = message.MessageType == AppleConstants.UsbmuxdMessageResult
            ? throw new UsbmuxdException("The usbmuxd daemon answered with the legacy protocol.")
            : UsbmuxdConnection.ParsePlistPayload(message);
        if (ReadResultCode(body) != AppleConstants.UsbmuxdResultOk)
        {
            throw new UsbmuxdException($"The usbmuxd daemon rejected the {messageType} request.",
                ReadResultCode(body));
        }

        return body;
    }

    private static uint ReadResultCode(Dictionary<string, object> body) =>
        body.TryGetValue("Number", out var value) && value is long number
            ? (uint)number
            : throw new UsbmuxdException("The usbmuxd plist response does not carry a result code.");

    private static UsbmuxdDevice ParseLegacyDeviceRecord(byte[] payload)
    {
        var deviceId = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var productId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
        var serialEnd = 6;
        while (serialEnd < 6 + 256 && payload[serialEnd] != 0)
        {
            serialEnd++;
        }

        var serial = Encoding.ASCII.GetString(payload, 6, serialEnd - 6);
        var location = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(264));
        return new UsbmuxdDevice(deviceId, serial, UsbmuxdDevice.ConnectionTypeUsb, productId, location);
    }

    private static string NormalizedSerial(string serial) => serial.Replace("-", string.Empty, StringComparison.Ordinal);

    private static ushort SwapU16(ushort value) => (ushort)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
}