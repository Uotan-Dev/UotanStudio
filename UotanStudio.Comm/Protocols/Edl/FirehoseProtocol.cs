using UotanStudio.Comm.Transports.Abstractions;

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
    /// Gets or sets the maximum payload size to target supported by the programmer (bytes);
    /// negotiated via the configure NAK attribute of the same name.
    /// <para>获取或设置 programmer 支持的到目标最大负载（字节）；通过同名 configure NAK
    /// 属性协商。</para>
    /// </summary>
    public long MaxPayloadSizeToTargetSupported { get; set; } = 1048576;

    /// <summary>
    /// Gets or sets the maximum payload from target (bytes).
    /// <para>获取或设置来自目标的最大负载（字节）。</para>
    /// </summary>
    public long MaxPayloadSizeFromTarget { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the sector size in bytes. 0 means auto-detect (after configure via
    /// &lt;getstorageinfo&gt;, falling back to a storage-type default).
    /// <para>获取或设置扇区大小（字节）。0 表示自动检测（configure 之后通过
    /// &lt;getstorageinfo&gt; 检测，失败则按存储类型回退默认值）。</para>
    /// </summary>
    public long SectorSize { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether the sector size should be auto-detected after configure.
    /// <para>获取或设置是否在 configure 之后自动检测扇区大小。</para>
    /// </summary>
    public bool AutoDetectSectorSize { get; set; } = true;

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
    /// Gets or sets the Verbose configure attribute.
    /// <para>获取或设置 configure 的 Verbose 属性。</para>
    /// </summary>
    public int Verbose { get; set; } = 0;

    /// <summary>
    /// Gets or sets the AlwaysValidate configure attribute.
    /// <para>获取或设置 configure 的 AlwaysValidate 属性。</para>
    /// </summary>
    public int AlwaysValidate { get; set; } = 0;

    /// <summary>
    /// Gets or sets the MaxDigestTableSizeInBytes configure attribute.
    /// <para>获取或设置 configure 的 MaxDigestTableSizeInBytes 属性。</para>
    /// </summary>
    public long MaxDigestTableSizeInBytes { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the SkipStorageInit configure attribute (non-zero to skip storage
    /// initialization).
    /// <para>获取或设置 configure 的 SkipStorageInit 属性（非零表示跳过存储初始化）。</para>
    /// </summary>
    public int SkipStorageInit { get; set; } = 0;

    /// <summary>
    /// Gets or sets the SkipWrite configure attribute (non-zero to simulate writes).
    /// <para>获取或设置 configure 的 SkipWrite 属性（非零表示模拟写入）。</para>
    /// </summary>
    public int SkipWrite { get; set; } = 0;

    /// <summary>
    /// Gets or sets the SkipSettingMinClocks configure attribute.
    /// <para>获取或设置 configure 的 SkipSettingMinClocks 属性。</para>
    /// </summary>
    public int SkipSettingMinClocks { get; set; } = 0;

    /// <summary>
    /// Gets or sets the SkipSettingMaxClocks configure attribute.
    /// <para>获取或设置 configure 的 SkipSettingMaxClocks 属性。</para>
    /// </summary>
    public int SkipSettingMaxClocks { get; set; } = 0;

    /// <summary>
    /// Gets or sets the TargetName configure attribute (optional).
    /// <para>获取或设置 configure 的 TargetName 属性（可选）。</para>
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets the StorageSlot configure attribute (0 = not emitted).
    /// <para>获取或设置 configure 的 StorageSlot 属性（0 表示不输出）。</para>
    /// </summary>
    public int StorageSlot { get; set; } = 0;

    /// <summary>
    /// Gets or sets the retry interval between configure attempts in milliseconds.
    /// <para>获取或设置 configure 尝试之间的重试间隔（毫秒）。</para>
    /// </summary>
    public int ConfigureRetryIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the per-attempt response timeout for configure in milliseconds.
    /// <para>获取或设置 configure 单次尝试的响应超时（毫秒）。</para>
    /// </summary>
    public int ConfigureResponseTimeoutMs { get; set; } = EdlConstants.ConfigureResponseTimeoutMs;

    /// <summary>
    /// Gets or sets the response timeout for the FIRST configure attempt in milliseconds.
    /// Right after the Sahara handshake the device may still be booting Firehose and ignore
    /// the first configure; a short timeout lets the retry kick in fast.
    /// <para>获取或设置首次 configure 尝试的响应超时（毫秒）。Sahara 握手刚结束时设备可能
    /// 仍在启动 Firehose 并忽略首个 configure；短超时让重试快速生效。</para>
    /// </summary>
    public int ConfigureFirstAttemptTimeoutMs { get; set; } = EdlConstants.ConfigureFirstAttemptTimeoutMs;

    /// <summary>
    /// Gets or sets the maximum number of consecutive configure timeouts before failing fast
    /// with ConnectionLost (0 = disabled, retry up to the attempt limit instead).
    /// <para>获取或设置连续 configure 超时后立即以 ConnectionLost 失败的上限（0 = 禁用，
    /// 按尝试次数上限重试）。</para>
    /// </summary>
    public int ConfigureMaxConsecutiveTimeouts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the budget in milliseconds for observing transport disconnection after
    /// a power ACK (0 disables observation). Disconnection does not prove successful boot.
    /// </summary>
    public int PowerResetVerifyTimeoutMs { get; set; } = EdlConstants.PowerResetVerifyTimeoutMs;

    /// <summary>Gets or sets the timeout in milliseconds for the power command's actual response.</summary>
    public int PowerResponseTimeoutMs { get; set; } = EdlConstants.PowerResponseTimeoutMs;

    /// <summary>
    /// Gets or sets an explicit write (ZLP) timeout in milliseconds (0 = per-storage default).
    /// <para>获取或设置显式写入（ZLP）超时（毫秒）（0 = 按存储类型默认值）。</para>
    /// </summary>
    public int WriteTimeoutMs { get; set; } = 0;

    /// <summary>
    /// Gets or sets an explicit erase timeout in milliseconds (0 = per-storage default).
    /// <para>获取或设置显式擦除超时（毫秒）（0 = 按存储类型默认值）。</para>
    /// </summary>
    public int EraseTimeoutMs { get; set; } = 0;

    /// <summary>
    /// Converts the storage type to its Firehose XML string.
    /// <para>转换存储类型为 Firehose XML 字符串。</para>
    /// </summary>
    public string StorageTypeString => StorageType switch
    {
        FirehoseStorageType.Ufs => "ufs",
        FirehoseStorageType.Nand => "nand",
        FirehoseStorageType.Spinor => "spinor",
        FirehoseStorageType.Emmc => "eMMC",
        FirehoseStorageType.Unknown => string.Empty,
        _ => "eMMC"
    };
}

/// <summary>
/// Implements the Firehose protocol: configure, program, erase, read, memory, power and UFS
/// provisioning operations.
/// <para>实现 Firehose 协议：配置、编程、擦除、读取、内存、电源与 UFS provisioning 操作。</para>
/// Operations on one instance are serialized through their final response. Do not modify
/// the configuration, share the transport with another protocol instance, or synchronously
/// wait for another operation from a log callback while an operation is in progress.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FirehoseProtocol"/> class.
/// <para>初始化 <see cref="FirehoseProtocol"/> 类的新实例。</para>
/// </remarks>
/// <param name="transport">The device transport. 设备传输。</param>
/// <param name="config">Optional Firehose configuration. 可选的 Firehose 配置。</param>
/// <param name="timeoutMs">Optional per-read command timeout in milliseconds. 可选的单次读取命令超时（毫秒）。</param>
public sealed partial class FirehoseProtocol(ICommTransport transport, FirehoseConfiguration? config = null, int? timeoutMs = null) : IDisposable
{
    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _cmdTimeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private readonly byte[] _rxBuffer = new byte[4 * 1024];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _rxBufferBytes;
    private volatile bool _disposed;
    private volatile bool _faulted;

    /// <summary>Gets whether an uncertain transfer or acknowledged power command requires a new device session.</summary>
    public bool RequiresReconnect => _faulted;

    /// <summary>
    /// Gets the current configuration.
    /// <para>获取当前配置。</para>
    /// </summary>
    public FirehoseConfiguration Configuration { get; } = config ?? new FirehoseConfiguration();

    /// <summary>
    /// Raised for log messages.
    /// <para>日志消息事件。</para>
    /// </summary>
    public event EventHandler<string>? LogMessage;

    /// <summary>
    /// Raised for log messages emitted by the target device inside Firehose XML blocks.
    /// <para>Firehose XML 块内目标设备发出的日志消息事件。</para>
    /// </summary>
    public event EventHandler<FirehoseLogEventArgs>? TargetLogReceived;

    /// <summary>
    /// Sends a configure command and negotiates payload sizes (and the sector size).
    /// <para>发送配置命令并协商负载大小（及扇区大小）。</para>
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts. 最大重试次数。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    /// <exception cref="EdlException">Thrown when configure fails or the device disconnects. 配置失败或设备断开时抛出。</exception>
    public async Task<FirehoseResponse> ConfigureAsync(int maxRetries = 30, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ConfigureCoreAsync(maxRetries, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Configures an already-owned transport; the caller must hold the operation lease.</summary>
    internal async Task<FirehoseResponse> ConfigureCoreAsync(int maxRetries, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                Log($"Configure attempt {attempt + 1}/{maxRetries}...");
                await Task.Delay(Configuration.ConfigureRetryIntervalMs, cancellationToken);
            }

            var xml = FirehoseXmlBuilder.BuildConfigure(Configuration);
            Log($"Sending configure: MaxPayloadSizeToTargetInBytes={Configuration.MaxPayloadSizeToTarget}");

            var firstAttempt = attempt == 0 && Configuration.ConfigureFirstAttemptTimeoutMs > 0;
            var perAttemptTimeoutMs = firstAttempt
                ? Configuration.ConfigureFirstAttemptTimeoutMs
                : (Configuration.ConfigureResponseTimeoutMs > 0 ? Configuration.ConfigureResponseTimeoutMs : (int?)null);

            FirehoseResponse response;
            try
            {
                response = await SendCommandAndWaitResponseAsync(xml, cancellationToken, perAttemptTimeoutMs, perAttemptTimeoutMs);
            }
            catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.TransportTimeout or EdlErrorCode.ReadPortTimeout)
            {
                throw new EdlException(EdlErrorCode.ConnectionLost,
                    "Configure timed out; reconnect rather than reusing a potentially delayed acknowledgement.", ex);
            }

            if (response.IsRawMode)
            {
                _faulted = true;
                throw new EdlException(EdlErrorCode.ConfigureFailed, "Configure unexpectedly entered raw mode.");
            }

            if (!response.IsAck)
            {
                var maxPayloadStr = response["MaxPayloadSizeToTargetInBytes"];
                if (maxPayloadStr != null && long.TryParse(maxPayloadStr, out var maxPayload) && maxPayload > 0 && maxPayload != Configuration.MaxPayloadSizeToTarget)
                {
                    Configuration.MaxPayloadSizeToTarget = maxPayload;
                    Log($"Retrying configure with MaxPayloadSizeToTargetInBytes={maxPayload}");
                    xml = FirehoseXmlBuilder.BuildConfigure(Configuration);
                    try
                    {
                        response = await SendCommandAndWaitResponseAsync(xml, cancellationToken, perAttemptTimeoutMs);
                    }
                    catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.TransportTimeout or EdlErrorCode.ReadPortTimeout)
                    {
                        throw new EdlException(EdlErrorCode.ConnectionLost, "Configure renegotiation timed out; reconnect.", ex);
                    }

                    if (!response.IsAck || response.IsRawMode)
                    {
                        _faulted = response.IsRawMode;
                        throw new EdlException(EdlErrorCode.ConfigureFailed,
                            $"Firehose configure failed. Response: {response.RawValue}");
                    }
                }
                else if (attempt < maxRetries - 1)
                {
                    continue;
                }
                else
                {
                    throw new EdlException(EdlErrorCode.ConfigureFailed,
                        $"Firehose configure failed after {maxRetries} attempts. Response: {response.RawValue}");
                }
            }

            var ackMax = response.GetInt64("MaxPayloadSizeToTargetInBytes");
            if (ackMax.HasValue)
            {
                Configuration.MaxPayloadSizeToTarget = ackMax.Value;
            }

            var ackMaxSupported = response.GetInt64("MaxPayloadSizeToTargetInBytesSupported");
            if (ackMaxSupported.HasValue)
            {
                Configuration.MaxPayloadSizeToTargetSupported = ackMaxSupported.Value;
            }

            var ackFromTarget = response.GetInt64("MaxPayloadSizeFromTargetInBytes");
            if (ackFromTarget.HasValue)
            {
                Configuration.MaxPayloadSizeFromTarget = ackFromTarget.Value;
            }

            Log($"Configure OK: MaxPayload={Configuration.MaxPayloadSizeToTarget}, FromTarget={Configuration.MaxPayloadSizeFromTarget}");

            if (Configuration.AutoDetectSectorSize && Configuration.SectorSize == 0)
            {
                await AutoDetectSectorSizeCoreAsync(cancellationToken);
            }
            else if (Configuration.SectorSize == 0)
            {
                Configuration.SectorSize = EdlConstants.DefaultSectorSizeSmall;
            }

            return response;
        }

        throw new EdlException(EdlErrorCode.ConfigureFailed,
            $"Firehose configure failed after {maxRetries} attempts (timeout).");
    }

    /// <summary>
    /// Auto-detects the sector size via &lt;getstorageinfo physical_partition_number="0"/&gt;
    /// and stores it in the configuration; falls back to a storage-type default.
    /// <para>通过 &lt;getstorageinfo physical_partition_number="0"/&gt; 自动检测扇区大小并存入
    /// 配置；失败时按存储类型回退默认值。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    public async Task AutoDetectSectorSizeAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await AutoDetectSectorSizeCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AutoDetectSectorSizeCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Configuration.StorageType == FirehoseStorageType.Nand)
        {
            return;
        }

        long sectorSize = 0;
        try
        {
            var storageInfo = await GetStorageInfoCoreAsync(0, cancellationToken);
            if (storageInfo.IsAck && (storageInfo.StorageGeometry?.SectorSize ?? storageInfo.GetInt64("sector_size")) is long detected && detected > 0)
            {
                sectorSize = detected;
                Log($"Sector size from getstorageinfo: {sectorSize}");
            }
            else
            {
                Log($"getstorageinfo returned no usable sector size (IsAck={storageInfo.IsAck}); using default.");
            }
        }
        catch (EdlException ex) when (!RequiresReconnect)
        {
            Log($"getstorageinfo failed ({ex.Message}); using default sector size.");
        }

        if (sectorSize <= 0)
        {
            sectorSize = Configuration.StorageType switch
            {
                FirehoseStorageType.Ufs or FirehoseStorageType.Spinor => EdlConstants.DefaultSectorSizeLarge,
                _ => EdlConstants.DefaultSectorSizeSmall
            };
            Log($"Using default sector size for {Configuration.StorageType}: {sectorSize}");
        }

        Configuration.SectorSize = sectorSize;
    }

    /// <summary>
    /// Sends an XML command and waits for its response.
    /// <para>发送 XML 命令并等待响应。</para>
    /// </summary>
    /// <param name="xml">The XML command. XML 命令。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <param name="responseTimeoutMs">Optional response timeout in milliseconds. 可选的响应超时（毫秒）。</param>
    /// <param name="writeTimeoutMs">Optional write timeout in milliseconds. 可选的写入超时（毫秒）。</param>
    /// <param name="logObserver">Optional bounded collector for logs belonging to this command.</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    private async Task<FirehoseResponse> SendCommandAndWaitResponseAsync(string xml, CancellationToken cancellationToken,
        int? responseTimeoutMs = null, int? writeTimeoutMs = null, Action<string>? logObserver = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
            await WriteDataAsync(xmlBytes, writeTimeoutMs ?? GetZlpTimeoutMs(), cancellationToken);
            return await ReceiveResponseAsync(cancellationToken, responseTimeoutMs, logObserver);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Gets the write (ZLP) timeout for the configured storage type.
    /// <para>获取按配置存储类型的写入（ZLP）超时。</para>
    /// </summary>
    /// <returns>The write timeout in milliseconds. 写入超时（毫秒）。</returns>
    private int GetZlpTimeoutMs()
    {
        return Configuration.WriteTimeoutMs > 0
            ? Configuration.WriteTimeoutMs
            : Configuration.StorageType switch
            {
                FirehoseStorageType.Spinor => EdlConstants.WriteTimeoutSpinorMs,
                FirehoseStorageType.Nand => EdlConstants.WriteTimeoutNandMs,
                FirehoseStorageType.Ufs => EdlConstants.WriteTimeoutUfsMs,
                _ => EdlConstants.WriteTimeoutDefaultMs
            };
    }

    /// <summary>
    /// Gets the erase timeout for the configured storage type.
    /// <para>获取按配置存储类型的擦除超时。</para>
    /// </summary>
    /// <returns>The erase timeout in milliseconds. 擦除超时（毫秒）。</returns>
    private int GetEraseTimeoutMs()
    {
        return Configuration.EraseTimeoutMs > 0
            ? Configuration.EraseTimeoutMs
            : Configuration.StorageType switch
            {
                FirehoseStorageType.Spinor => EdlConstants.EraseTimeoutSpinorMs,
                FirehoseStorageType.Nand => EdlConstants.EraseTimeoutNandMs,
                FirehoseStorageType.Ufs => EdlConstants.EraseTimeoutUfsMs,
                _ => EdlConstants.EraseTimeoutEmmcMs
            };
    }

    /// <summary>
    /// Gets the effective slot number emitted on program/read/erase commands.
    /// <para>获取 program/read/erase 命令输出的有效槽位编号。</para>
    /// </summary>
    private int? EffectiveSlot => Configuration.Slot != 0 ? Configuration.Slot : null;

    /// <summary>
    /// Writes data to the transport.
    /// <para>将数据写入传输。</para>
    /// </summary>
    /// <param name="data">The data to write. 要写入的数据。</param>
    /// <param name="timeoutMs">The write timeout in milliseconds. 写入超时（毫秒）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    private async Task WriteDataAsync(byte[] data, int timeoutMs, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var w = await _transport.WriteAsync(data, 0, data.Length, timeoutMs, cancellationToken);
        if (w != data.Length)
        {
            throw new EdlException(EdlErrorCode.WritePortTimeout);
        }
    }

    /// <summary>
    /// Logs a Message via the <see cref="LogMessage"/> event.
    /// <para>通过 <see cref="LogMessage"/> 事件记录消息。</para>
    /// </summary>
    /// <param name="msg">The Message to log. 要记录的消息。</param>
    private void Log(string msg) => LogMessage?.Invoke(this, msg);

    /// <summary>
    /// Surfaces a target log via the <see cref="LogMessage"/> and <see cref="TargetLogReceived"/>
    /// events.
    /// <para>通过 <see cref="LogMessage"/> 与 <see cref="TargetLogReceived"/> 事件上报目标日志。</para>
    /// </summary>
    /// <param name="value">The target log value. 目标日志值。</param>
    private void OnTargetLog(string value)
    {
        Log($"TARGET: {value}");
        TargetLogReceived?.Invoke(this, new FirehoseLogEventArgs(value));
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the protocol has been disposed.
    /// <para>如果协议已释放，则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FirehoseProtocol));
        }

        if (_faulted)
        {
            throw new EdlException(EdlErrorCode.ConnectionLost,
                "The Firehose session is no longer reusable. Close and reconnect the transport before continuing.");
        }
    }

    /// <summary>Reserves the transport, including the Sahara-to-Firehose initialization transition.</summary>
    internal async ValueTask<OperationLease> EnterOperationAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The previous owner may have faulted or been disposed while this call waited.
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return new OperationLease(_operationGate);
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    /// <summary>Marks an uncertain initialization as unusable until the transport is reconnected.</summary>
    internal void Invalidate() => _faulted = true;

    /// <summary>Releases one transport transaction without disposing a gate still used by waiters.</summary>
    internal sealed class OperationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private long ValidateStorageRange(long startSector, long numSectors, int physicalPartition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numSectors);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        if (Configuration.SectorSize is <= 0 or > 65536)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "A sector size between 1 and 65536 bytes is required.");
        }

        _ = checked(startSector + numSectors);
        return checked(numSectors * Configuration.SectorSize);
    }

    private int GetProgramPayloadSize()
    {
        if (Configuration.MaxPayloadSizeToTarget < Configuration.SectorSize)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "The negotiated payload cannot hold one sector.");
        }

        var bounded = Math.Min(Configuration.MaxPayloadSizeToTarget, 1024 * 1024);
        return checked((int)(bounded - (bounded % Configuration.SectorSize)));
    }

    private static void RequireRawMode(FirehoseResponse response, EdlErrorCode errorCode)
    {
        if (!response.IsAck || !response.IsRawMode)
        {
            throw new EdlException(errorCode, "The target did not acknowledge entry into raw mode.");
        }
    }

    /// <summary>
    /// Disposes the protocol.
    /// <para>释放协议。</para>
    /// </summary>
    // Do not dispose the semaphore while active owners or queued callers may still use it.
    public void Dispose() => _disposed = true;
}