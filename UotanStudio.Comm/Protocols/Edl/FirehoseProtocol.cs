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
    /// Gets or sets the budget (ms) for verifying that a power reset actually took effect by
    /// reading target logs until the device disappears (0 = disabled).
    /// <para>获取或设置通过读取目标日志直到设备消失来验证电源复位是否生效的预算（毫秒）
    /// （0 = 禁用）。</para>
    /// </summary>
    public int PowerResetVerifyTimeoutMs { get; set; } = EdlConstants.PowerResetVerifyTimeoutMs;

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
        // Unknown 存储类型无法生成有效 XML，返回空让调用方处理。
        // Unknown storage type cannot produce valid XML; return empty for the caller to handle.
        FirehoseStorageType.Unknown => string.Empty,
        _ => "eMMC"
    };
}

/// <summary>
/// Implements the Firehose protocol: configure, program, erase, read, memory, power and UFS
/// provisioning operations.
/// <para>实现 Firehose 协议：配置、编程、擦除、读取、内存、电源与 UFS provisioning 操作。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FirehoseProtocol"/> class.
/// <para>初始化 <see cref="FirehoseProtocol"/> 类的新实例。</para>
/// </remarks>
/// <param name="transport">The device transport. 设备传输。</param>
/// <param name="config">Optional Firehose configuration. 可选的 Firehose 配置。</param>
/// <param name="timeoutMs">Optional per-read command timeout in milliseconds. 可选的单次读取命令超时（毫秒）。</param>
public sealed partial class FirehoseProtocol(ICommTransport transport, FirehoseConfiguration? config = null, int? timeoutMs = null)
{
    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _cmdTimeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private readonly byte[] _rxBuffer = new byte[4 * 1024];
    private int _rxBufferBytes;
    private bool _disposed;

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
        ThrowIfDisposed();

        var maxConsecutiveTimeouts = Configuration.ConfigureMaxConsecutiveTimeouts;

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

            // Right after the Sahara handshake the device may still be booting Firehose and
            // ignore the first configure (both the response read AND the command write — the
            // OUT endpoint may NAK): the FIRST attempt uses the short first-attempt timeout
            // for BOTH directions so the retry kicks in fast; later attempts use the generous
            // configure timeout.
            // Sahara 握手刚结束时设备可能仍在启动 Firehose 并忽略首个 configure（响应读取
            // 与命令写入皆然——OUT 端点可能 NAK）：首次尝试的读、写两个方向都使用短超时，
            // 让重试快速生效；后续尝试使用宽松的 configure 超时。
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
                // Consecutive timeouts reaching the limit mean the device is gone (reset /
                // unplugged); fail fast with ConnectionLost instead of idling maxRetries ×
                // the full response timeout.
                // 连续超时达到上限说明设备已消失（复位/拔出）；立即以 ConnectionLost 失败，
                // 而不是空等 maxRetries × 完整响应超时。
                if (maxConsecutiveTimeouts > 0 && attempt + 1 >= maxConsecutiveTimeouts)
                {
                    throw new EdlException(EdlErrorCode.ConnectionLost,
                        $"Configure timed out {attempt + 1} consecutive times; device disconnected.");
                }

                continue;
            }

            if (response.IsRawMode)
            {
                Log("Device entered raw mode after configure. Draining pending data...");
                await DrainPendingDataAsync(cancellationToken);
                continue;
            }

            if (!response.IsAck)
            {
                var maxPayloadStr = response["MaxPayloadSizeToTargetInBytes"];
                if (maxPayloadStr != null && long.TryParse(maxPayloadStr, out var maxPayload) && maxPayload > 0 && maxPayload != Configuration.MaxPayloadSizeToTarget)
                {
                    // The programmer rejected our payload size and told us the supported one:
                    // resend the configure IMMEDIATELY with the corrected payload instead of
                    // looping through another retry interval.
                    // programmer 拒绝了我方负载大小并告知支持值：立即使用修正后的负载重发
                    // configure，而不是再空转一轮重试间隔。
                    Configuration.MaxPayloadSizeToTarget = maxPayload;
                    Log($"Retrying configure with MaxPayloadSizeToTargetInBytes={maxPayload}");
                    xml = FirehoseXmlBuilder.BuildConfigure(Configuration);
                    try
                    {
                        response = await SendCommandAndWaitResponseAsync(xml, cancellationToken, perAttemptTimeoutMs);
                    }
                    catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.TransportTimeout or EdlErrorCode.ReadPortTimeout)
                    {
                        continue;
                    }

                    if (!response.IsAck)
                    {
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

            // Sector-size auto-detection: prefer <getstorageinfo>'s sector_size; fall back to
            // a storage-type default (UFS/Spinor → 4096, else 512).
            // 扇区大小自动检测：优先使用 <getstorageinfo> 的 sector_size；失败则按存储类型
            // 回退（UFS/Spinor → 4096，其余 → 512）。
            if (Configuration.AutoDetectSectorSize && Configuration.SectorSize == 0)
            {
                await AutoDetectSectorSizeAsync(cancellationToken);
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
        ThrowIfDisposed();
        // NAND programmers do not support getstorageinfo; keep the configured size.
        // NAND programmer 不支持 getstorageinfo；保留配置的扇区大小。
        if (Configuration.StorageType == FirehoseStorageType.Nand)
        {
            return;
        }

        long sectorSize = 0;
        try
        {
            var storageInfo = await GetStorageInfoAsync(0, cancellationToken);
            if (storageInfo.IsAck && storageInfo.GetInt64("sector_size") is long detected && detected > 0)
            {
                sectorSize = detected;
                Log($"Sector size from getstorageinfo: {sectorSize}");
            }
            else
            {
                Log($"getstorageinfo returned no usable sector size (IsAck={storageInfo.IsAck}); using default.");
            }
        }
        catch (EdlException ex)
        {
            Log($"getstorageinfo failed ({ex.Message}); using default sector size.");
        }

        if (sectorSize <= 0)
        {
            sectorSize = Configuration.StorageType switch
            {
                FirehoseStorageType.Ufs or FirehoseStorageType.Spinor => EdlConstants.DefaultSectorSizeLarge,
                // eMMC/NAND/Unknown 使用小扇区（512）作为保守默认值。
                // eMMC/NAND/Unknown use the small sector size (512) as a conservative default.
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
    /// <returns>The Firehose response. Firehose 响应。</returns>
    private async Task<FirehoseResponse> SendCommandAndWaitResponseAsync(string xml, CancellationToken cancellationToken, int? responseTimeoutMs = null, int? writeTimeoutMs = null)
    {
        var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
        // Command writes use the generous per-storage write timeout: a short write timeout
        // loses the command when the device is busy (just entered Firehose, or right after a
        // large raw read), leaving the read side waiting for the full response timeout.
        // 命令写入使用宽松的按存储类型写入超时：短写超时会在设备忙时（刚进入 Firehose 或
        // 大块 raw 读取刚结束）丢失命令，导致读侧空等完整响应超时。
        await WriteDataAsync(xmlBytes, writeTimeoutMs ?? GetZlpTimeoutMs(), cancellationToken);
        return await ReceiveResponseAsync(cancellationToken, responseTimeoutMs);
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
                // eMMC/Unknown 使用默认写入超时。
                // eMMC/Unknown use the default write timeout.
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
                // eMMC/Unknown 使用 eMMC 擦除超时作为默认值。
                // eMMC/Unknown use the eMMC erase timeout as the default.
                _ => EdlConstants.EraseTimeoutEmmcMs
            };
    }

    /// <summary>
    /// Gets the effective maximum payload size from the target (at least 4096 bytes).
    /// <para>获取来自目标的有效最大负载大小（至少 4096 字节）。</para>
    /// </summary>
    private int EffectiveMaxPayloadFromTarget => (int)Math.Max(Configuration.MaxPayloadSizeFromTarget, 4096);

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
        var w = await _transport.WriteAsync(data, 0, data.Length, timeoutMs, cancellationToken);
        if (w != data.Length)
        {
            throw new EdlException(EdlErrorCode.WritePortTimeout);
        }
    }

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes, returning only the bytes actually received.
    /// <para>读取最多 <paramref name="length"/> 个字节，仅返回实际收到的字节。</para>
    /// </summary>
    /// <param name="length">The maximum number of bytes to read. 最多读取的字节数。</param>
    /// <param name="timeoutMs">The read timeout in milliseconds. 读取超时（毫秒）。</param>
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
    }

    /// <summary>
    /// Disposes the protocol.
    /// <para>释放协议。</para>
    /// </summary>
    public void Dispose() => _disposed = true;
}