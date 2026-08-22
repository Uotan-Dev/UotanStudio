using System.Text;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Storage type enumeration for Firehose protocol.
/// <para>Firehose 协议存储类型枚举。</para>
/// </summary>
public enum FirehoseStorageType
{
    /// <summary>
    /// eMMC storage.
    /// <para>eMMC 存储。</para>
    /// </summary>
    Emmc,

    /// <summary>
    /// UFS storage.
    /// <para>UFS 存储。</para>
    /// </summary>
    Ufs,

    /// <summary>
    /// NAND storage.
    /// <para>NAND 存储。</para>
    /// </summary>
    Nand,

    /// <summary>
    /// SPI NOR storage.
    /// <para>SPI NOR 存储。</para>
    /// </summary>
    Spinor,

    /// <summary>
    /// Unknown storage type.
    /// <para>未知存储类型。</para>
    /// </summary>
    Unknown
}

/// <summary>
/// Firehose protocol configuration.
/// <para>Firehose 协议配置。</para>
/// </summary>
public class FirehoseConfiguration
{
    /// <summary>
    /// Gets or sets the storage type.
    /// <para>获取或设置存储类型。</para>
    /// </summary>
    public FirehoseStorageType StorageType { get; set; } = FirehoseStorageType.Emmc;

    /// <summary>
    /// Gets or sets the maximum payload to target (bytes).
    /// <para>获取或设置到目标的最大负载（字节）。</para>
    /// </summary>
    public long MaxPayloadSizeToTarget { get; set; } = 1048576;

    /// <summary>
    /// Gets or sets the maximum payload from target (bytes).
    /// <para>获取或设置来自目标的最大负载（字节）。</para>
    /// </summary>
    public long MaxPayloadSizeFromTarget { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the sector size in bytes.
    /// <para>获取或设置扇区大小（字节）。</para>
    /// </summary>
    public long SectorSize { get; set; } = 512;

    /// <summary>
    /// Gets or sets whether the host is ZLP-aware.
    /// <para>主机是否 ZLP 感知。</para>
    /// </summary>
    public bool ZlpAwareHost { get; set; } = true;

    /// <summary>
    /// Gets or sets the response timeout (seconds).
    /// <para>获取或设置响应超时（秒）。</para>
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the number of A/B slots (0 = not used).
    /// <para>获取或设置 A/B 槽位编号（0 表示未使用）。</para>
    /// </summary>
    public int Slot { get; set; } = 0;

    /// <summary>
    /// Converts the storage type to its Firehose XML string.
    /// <para>转换存储类型为 Firehose XML 字符串。</para>
    /// </summary>
    public string StorageTypeString => StorageType switch
    {
        FirehoseStorageType.Ufs => "ufs",
        FirehoseStorageType.Nand => "nand",
        FirehoseStorageType.Spinor => "spinor",
        _ => "eMMC"
    };
}

/// <summary>
/// Implements the Firehose protocol: configure, program, erase, read, and power operations.
/// <para>实现 Firehose 协议：配置、编程、擦除、读取和电源操作。</para>
/// </summary>
public sealed class FirehoseProtocol
{
    private readonly IUsbDeviceSession _transport;
    private FirehoseConfiguration _config;
    private readonly byte[] _rxBuffer;
    private int _rxLen;
    private bool _disposed;

    /// <summary>
    /// Gets the current configuration.
    /// <para>获取当前配置。</para>
    /// </summary>
    public FirehoseConfiguration Configuration => _config;

    /// <summary>
    /// Raised for log messages.
    /// <para>日志消息事件。</para>
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Initializes a new instance of the <see cref="FirehoseProtocol"/> class.
    /// <para>初始化 <see cref="FirehoseProtocol"/> 类的新实例。</para>
    /// </summary>
    /// <param name="transport">The USB transport session. USB 传输会话。</param>
    /// <param name="config">Optional Firehose configuration. 可选的 Firehose 配置。</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
    public FirehoseProtocol(IUsbDeviceSession transport, FirehoseConfiguration? config = null, int? timeoutMs = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _config = config ?? new FirehoseConfiguration();
        _rxBuffer = new byte[4 * 1024];
    }

    /// <summary>
    /// Sends a configure command and negotiates payload sizes.
    /// <para>发送配置命令并协商负载大小。</para>
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts. 最大重试次数。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    /// <exception cref="FirehoseException">Thrown when configure fails. 配置失败时抛出。</exception>
    public async Task<FirehoseResponse> ConfigureAsync(int maxRetries = 30, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
                await Task.Delay(1000, cancellationToken);

            var xml = BuildConfigureXml();
            FirehoseResponse response;
            try
            {
                response = await SendCommandAndWaitResponseAsync(xml, cancellationToken, attempt == 0 ? 3000 : 15000).ConfigureAwait(false);
            }
            catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.TransportTimeout || ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
            {
                continue;
            }

            if (response.IsRawMode)
            {
                await DrainPendingDataAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsAck)
            {
                var maxPayloadStr = response["MaxPayloadSizeToTargetInBytes"];
                if (maxPayloadStr != null && long.TryParse(maxPayloadStr, out var maxPayload) && maxPayload > 0 && maxPayload != _config.MaxPayloadSizeToTarget)
                {
                    _config.MaxPayloadSizeToTarget = maxPayload;
                    continue;
                }
                throw new FirehoseException($"Configure failed: {response.RawValue}");
            }

            var ackMax = response.GetInt64("MaxPayloadSizeToTargetInBytes");
            if (ackMax.HasValue) _config.MaxPayloadSizeToTarget = ackMax.Value;

            var ackFromTarget = response.GetInt64("MaxPayloadSizeFromTargetInBytes");
            if (ackFromTarget.HasValue) _config.MaxPayloadSizeFromTarget = ackFromTarget.Value;

            Log($"Configure OK: MaxPayload={_config.MaxPayloadSizeToTarget}, FromTarget={_config.MaxPayloadSizeFromTarget}");
            return response;
        }

        throw new FirehoseException($"Configure failed after {maxRetries} attempts.");
    }

    /// <summary>
    /// Programs (flashes) data to a partition.
    /// <para>将数据编程（刷写）到分区。</para>
    /// </summary>
    /// <param name="dataStream">The data stream to write. 要写入的数据流。</param>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> ProgramAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = BuildProgramXml(startSector, numSectors, filename, physicalPartition);
        await WriteDataAsync(Encoding.UTF8.GetBytes(xml), cancellationToken);
        Log($"Sent <program> for {filename}, sectors={numSectors}");

        var totalBytes = numSectors * _config.SectorSize;
        var maxPayload = (int)Math.Min(_config.MaxPayloadSizeToTarget, int.MaxValue);
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(maxPayload);
        try
        {
            long bytesWritten = 0;
            while (bytesWritten < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(maxPayload, totalBytes - bytesWritten);
                var n = await dataStream.ReadAsync(buf, 0, toRead, cancellationToken);
                if (n == 0) break;
                await WriteDataAsync(buf.AsMemory(0, n), cancellationToken);
                bytesWritten += n;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }

        return await ReceiveResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Erases sectors from a partition.
    /// <para>擦除分区中的扇区。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> EraseAsync(long startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = BuildEraseXml(startSector, numSectors, physicalPartition);
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken, 30000).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads data from a partition.
    /// <para>从分区读取数据。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A tuple containing the response and data. 包含响应和数据的元组。</returns>
    public async Task<(FirehoseResponse Response, byte[] Data)> ReadAsync(long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = BuildReadXml(startSector, numSectors, filename, physicalPartition);
        await WriteDataAsync(Encoding.UTF8.GetBytes(xml), cancellationToken);

        var totalBytes = numSectors * _config.SectorSize;
        using var ms = new System.IO.MemoryStream();
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var remaining = totalBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(65536, remaining);
                var n = _transport.ReadInto(buf, 0, toRead, _config.ResponseTimeoutSeconds * 1000);
                if (n == 0) throw new EdlException(EdlErrorCode.ReadPortTimeout);
                ms.Write(buf, 0, n);
                remaining -= n;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }

        FirehoseResponse response = await ReceiveResponseAsync(cancellationToken).ConfigureAwait(false);
        return (response, ms.ToArray());
    }

    /// <summary>
    /// Resets the device.
    /// <para>复位设备。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = EdlConstants.XmlHeader + EdlConstants.DataOpen + "<power value=\"reset\" />\n" + EdlConstants.DataClose;
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken, 3000).ConfigureAwait(false);
    }

    /// <summary>
    /// Powers off the device.
    /// <para>关闭设备电源。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> PowerOffAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = EdlConstants.XmlHeader + EdlConstants.DataOpen + "<power value=\"off\" />\n" + EdlConstants.DataClose;
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken, 3000).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the configure XML command.
    /// <para>构建配置 XML 命令。</para>
    /// </summary>
    /// <returns>The configure XML string. 配置 XML 字符串。</returns>
    private string BuildConfigureXml()
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<configure MemoryName=\"{_config.StorageTypeString}\"");
        sb.Append($" MaxPayloadSizeToTargetInBytes=\"{_config.MaxPayloadSizeToTarget}\"");
        sb.Append($" ZlpAwareHost=\"{(_config.ZlpAwareHost ? 1 : 0)}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the program XML command.
    /// <para>构建编程 XML 命令。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <returns>The program XML string. 编程 XML 字符串。</returns>
    private string BuildProgramXml(long startSector, long numSectors, string filename, int physicalPartition)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<program SECTOR_SIZE_IN_BYTES=\"{_config.SectorSize}\"");
        sb.Append($" num_partition_sectors=\"{numSectors}\"");
        sb.Append($" filename=\"{System.Security.SecurityElement.Escape(filename)}\"");
        sb.Append($" physical_partition_number=\"{physicalPartition}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (_config.Slot != 0)
            sb.Append($" slot=\"{_config.Slot}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the erase XML command.
    /// <para>构建擦除 XML 命令。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <returns>The erase XML string. 擦除 XML 字符串。</returns>
    private string BuildEraseXml(long startSector, long numSectors, int physicalPartition)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<erase SECTOR_SIZE_IN_BYTES=\"{_config.SectorSize}\"");
        sb.Append($" num_partition_sectors=\"{numSectors}\"");
        sb.Append($" physical_partition_number=\"{physicalPartition}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (_config.Slot != 0)
            sb.Append($" slot=\"{_config.Slot}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the read XML command.
    /// <para>构建读取 XML 命令。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="filename">The target filename. 目标文件名。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <returns>The read XML string. 读取 XML 字符串。</returns>
    private string BuildReadXml(long startSector, long numSectors, string filename, int physicalPartition)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append($"<read SECTOR_SIZE_IN_BYTES=\"{_config.SectorSize}\"");
        sb.Append($" num_partition_sectors=\"{numSectors}\"");
        sb.Append($" filename=\"{System.Security.SecurityElement.Escape(filename)}\"");
        sb.Append($" physical_partition_number=\"{physicalPartition}\"");
        sb.Append($" start_sector=\"{startSector}\"");
        if (_config.Slot != 0)
            sb.Append($" slot=\"{_config.Slot}\"");
        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Sends an XML command and waits for the response.
    /// <para>发送 XML 命令并等待响应。</para>
    /// </summary>
    /// <param name="xml">The XML command. XML 命令。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    private async Task<FirehoseResponse> SendCommandAndWaitResponseAsync(string xml, CancellationToken cancellationToken, int? timeoutMs = null)
    {
        await WriteDataAsync(Encoding.UTF8.GetBytes(xml), cancellationToken);
        return await ReceiveResponseAsync(cancellationToken, timeoutMs).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives and parses a Firehose response.
    /// <para>接收并解析 Firehose 响应。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds. 可选的超时（毫秒）。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    /// <exception cref="FirehoseException">Thrown on timeout. 超时时抛出。</exception>
    private async Task<FirehoseResponse> ReceiveResponseAsync(CancellationToken cancellationToken, int? timeoutMs = null)
    {
        ThrowIfDisposed();
        var sb = new StringBuilder();
        var start = Environment.TickCount64;
        var timeout = timeoutMs ?? (_config.ResponseTimeoutSeconds * 1000);

        while ((Environment.TickCount64 - start) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n;
            try
            {
                n = _transport.ReadInto(_rxBuffer, _rxLen, _rxBuffer.Length - _rxLen, Math.Max(timeout / 10, 500));
            }
            catch (EdlException)
            {
                break;
            }
            if (n == 0) continue;
            _rxLen += n;

            sb.Append(Encoding.UTF8.GetString(_rxBuffer, 0, _rxLen));

            var current = sb.ToString();
            var dataEnd = current.IndexOf("</data>", StringComparison.Ordinal);
            if (dataEnd >= 0)
            {
                // Extract first complete <data> block
                var dataStart = current.IndexOf("<data>", StringComparison.Ordinal);
                if (dataStart >= 0 && dataStart < dataEnd)
                {
                    var xml = current.Substring(dataStart, dataEnd + 7 - dataStart);
                    // Shift remaining bytes
                    _rxLen = 0;
                    return FirehoseResponse.Parse(xml);
                }
            }
        }

        throw new FirehoseException("Firehose response timeout.");
    }

    /// <summary>
    /// Drains any pending data from the transport.
    /// <para>排出传输中的任何待处理数据。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task DrainPendingDataAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var n = _transport.ReadInto(_rxBuffer, 0, _rxBuffer.Length, 500);
                if (n == 0) break;
            }
        }
        catch { }
        await Task.CompletedTask;
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
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(buf);
            var w = await Task.Run(() => _transport.Write(buf, 0, data.Length, 120000), cancellationToken);
            if (w != data.Length) throw new EdlException(EdlErrorCode.WritePortTimeout);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
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
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(FirehoseProtocol)); }

    /// <summary>
    /// Disposes the protocol.
    /// <para>释放协议。</para>
    /// </summary>
    public void Dispose() { _disposed = true; }
}
