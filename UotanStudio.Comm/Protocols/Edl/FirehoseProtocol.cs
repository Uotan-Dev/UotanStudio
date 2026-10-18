using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Common.Evidence;
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
    /// <value>The target storage technology; defaults to <see cref="FirehoseStorageType.Emmc"/>. <para>目标存储技术；默认为 <see cref="FirehoseStorageType.Emmc"/>。</para></value>
    public FirehoseStorageType StorageType { get; set; } = FirehoseStorageType.Emmc;

    /// <summary>
    /// Gets or sets the maximum payload to target (bytes).
    /// <para>获取或设置到目标的最大负载（字节）。</para>
    /// </summary>
    /// <value>The negotiated write chunk size in bytes; defaults to 1048576. <para>协商后的写入块大小（字节）；默认 1048576。</para></value>
    public long MaxPayloadSizeToTarget { get; set; } = 1048576;

    /// <summary>
    /// Gets or sets the maximum payload size to target supported by the programmer (bytes);
    /// negotiated via the configure NAK attribute of the same name.
    /// <para>获取或设置 programmer 支持的到目标最大负载（字节）；通过同名 configure NAK
    /// 属性协商。</para>
    /// </summary>
    /// <value>The upper bound reported by the programmer; defaults to 1048576. <para>programmer 报告的上限；默认 1048576。</para></value>
    public long MaxPayloadSizeToTargetSupported { get; set; } = 1048576;

    /// <summary>
    /// Gets or sets the explicit upper bound for one program write chunk in bytes. The effective chunk
    /// is the smaller of this bound and the negotiated <see cref="MaxPayloadSizeToTarget"/>, so raising
    /// it only exposes more of what the target already agreed to.
    /// <para>获取或设置单个编程写入块（字节）的显式上限。实际块大小取本上限与协商后的
    /// <see cref="MaxPayloadSizeToTarget"/> 的较小值，因此上调只会暴露目标已同意的更多容量。</para>
    /// </summary>
    /// <value>The write-chunk bound in bytes; defaults to 1048576, the historical hard cap. Values must
    /// be a multiple of 512 between 512 and 16777216 inclusive.
    /// <para>写入块上限（字节）；默认 1048576，即历史硬上限。取值须为 512 的倍数且介于 512 与
    /// 16777216（含）之间。</para></value>
    public long MaxProgramChunkBytes { get; set; } = 1048576;

    /// <summary>
    /// Gets or sets the NAND <c>PAGES_PER_BLOCK</c> configure attribute, emitted into configure only
    /// when the storage is NAND. Zero omits the attribute.
    /// <para>获取或设置 NAND 的 <c>PAGES_PER_BLOCK</c> configure 属性，仅在存储为 NAND 时写入；
    /// 为 0 时省略该属性。</para>
    /// </summary>
    /// <value>The pages per NAND block, 1 through 65535 when emitted; 0 (default) omits the attribute.
    /// <para>NAND 每块页数；输出时为 1 至 65535；默认 0 表示省略该属性。</para></value>
    public long NandPagesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets whether read and write command headers may proceed without a target response, for loaders that never acknowledge reads or writes. The raw data and final acknowledgement are still consumed; only the status-only erase header tolerates the absent acknowledgement. No timeout is shortened.
    /// <para>获取或设置读/写命令头是否可在无目标响应的情况下继续，用于从不确认读写操作的 loader。原始数据与最终确认仍会消费；仅状态性的 erase 头容忍缺失的确认。不缩短任何超时。</para>
    /// </summary>
    /// <value>False by default (strict waiting); true lets a status-only erase command complete without
    /// reading a response. <para>默认为 false（严格等待）；为 true 时允许仅状态的 erase 命令在不读取
    /// 响应的情况下完成。</para></value>
    public bool SkipResponseOnErase { get; set; }

    /// <summary>
    /// Gets or sets whether <c>&lt;program&gt;</c> and <c>&lt;read&gt;</c> headers may proceed without reading the raw-mode acknowledgement, for loaders that never send a header response. The raw payload and final acknowledgement are still consumed and no timeout is shortened.
    /// <para>获取或设置 <c>&lt;program&gt;</c> 与 <c>&lt;read&gt;</c> 命令头是否可在不读取原始模式确认的情况下继续，用于从不发送头响应的 loader。原始载荷与最终确认仍会照旧消费，且不缩短任何超时。</para>
    /// </summary>
    /// <value>False by default (strict waiting); true skips only the program/read header wait.
    /// <para>默认为 false（严格等待）；为 true 时仅跳过 program/read 的头等待。</para></value>
    public bool SkipResponseOnReadWrite { get; set; }

    /// <summary>
    /// Gets or sets the maximum payload from target (bytes).
    /// <para>获取或设置来自目标的最大负载（字节）。</para>
    /// </summary>
    /// <value>The negotiated read chunk size in bytes; defaults to 8192. <para>协商后的读取块大小（字节）；默认 8192。</para></value>
    public long MaxPayloadSizeFromTarget { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the sector size in bytes. 0 means auto-detect (after configure via
    /// &lt;getstorageinfo&gt;, falling back to a storage-type default).
    /// <para>获取或设置扇区大小（字节）。0 表示自动检测（configure 之后通过
    /// &lt;getstorageinfo&gt; 检测，失败则按存储类型回退默认值）。</para>
    /// </summary>
    /// <value>The sector size in bytes; 0 requests auto-detection. <para>扇区大小（字节）；0 表示请求自动检测。</para></value>
    public long SectorSize { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether the sector size should be auto-detected after configure.
    /// <para>获取或设置是否在 configure 之后自动检测扇区大小。</para>
    /// </summary>
    /// <value><see langword="true"/> to run <c>getstorageinfo</c> when <see cref="SectorSize"/> is 0; defaults to <see langword="true"/>. <para>当 <see cref="SectorSize"/> 为 0 时是否运行 <c>getstorageinfo</c>；默认为 <see langword="true"/>。</para></value>
    public bool AutoDetectSectorSize { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the host is ZLP-aware.
    /// <para>主机是否 ZLP 感知。</para>
    /// </summary>
    /// <value><see langword="true"/> when the host handles zero-length packets; defaults to <see langword="true"/>. <para>主机是否可处理零长包；默认为 <see langword="true"/>。</para></value>
    public bool ZlpAwareHost { get; set; } = true;

    /// <summary>
    /// Gets or sets the response timeout (seconds).
    /// <para>获取或设置响应超时（秒）。</para>
    /// </summary>
    /// <value>The response timeout in seconds; defaults to 120. <para>响应超时（秒）；默认 120。</para></value>
    public int ResponseTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the number of A/B slots (0 = not used).
    /// <para>获取或设置 A/B 槽位编号（0 表示未使用）。</para>
    /// </summary>
    /// <value>The slot emitted on program/read/erase commands; 0 omits it. <para>program/read/erase 命令输出的槽位编号；0 表示省略。</para></value>
    public int Slot { get; set; } = 0;

    /// <summary>
    /// Gets or sets the Verbose configure attribute.
    /// <para>获取或设置 configure 的 Verbose 属性。</para>
    /// </summary>
    /// <value>The Verbose value sent in the configure command; defaults to 0. <para>configure 命令中发送的 Verbose 值；默认 0。</para></value>
    public int Verbose { get; set; } = 0;

    /// <summary>
    /// Gets or sets the AlwaysValidate configure attribute.
    /// <para>获取或设置 configure 的 AlwaysValidate 属性。</para>
    /// </summary>
    /// <value>The AlwaysValidate value sent in the configure command; defaults to 0. <para>configure 命令中发送的 AlwaysValidate 值；默认 0。</para></value>
    public int AlwaysValidate { get; set; } = 0;

    /// <summary>
    /// Gets or sets the MaxDigestTableSizeInBytes configure attribute.
    /// <para>获取或设置 configure 的 MaxDigestTableSizeInBytes 属性。</para>
    /// </summary>
    /// <value>The digest table size budget in bytes; defaults to 8192. <para>摘要表大小预算（字节）；默认 8192。</para></value>
    public long MaxDigestTableSizeInBytes { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the SkipStorageInit configure attribute (non-zero to skip storage
    /// initialization).
    /// <para>获取或设置 configure 的 SkipStorageInit 属性（非零表示跳过存储初始化）。</para>
    /// </summary>
    /// <value>Non-zero skips storage initialization; defaults to 0. <para>非零则跳过存储初始化；默认 0。</para></value>
    public int SkipStorageInit { get; set; } = 0;

    /// <summary>
    /// Gets or sets the SkipWrite configure attribute (non-zero to simulate writes).
    /// <para>获取或设置 configure 的 SkipWrite 属性（非零表示模拟写入）。</para>
    /// </summary>
    /// <value>Non-zero simulates writes; defaults to 0. <para>非零则模拟写入；默认 0。</para></value>
    public int SkipWrite { get; set; } = 0;

    /// <summary>
    /// Gets or sets the SkipSettingMinClocks configure attribute.
    /// <para>获取或设置 configure 的 SkipSettingMinClocks 属性。</para>
    /// </summary>
    /// <value>Non-zero skips setting minimum clocks; defaults to 0. <para>非零则跳过设置最低时钟；默认 0。</para></value>
    public int SkipSettingMinClocks { get; set; } = 0;

    /// <summary>
    /// Gets or sets the SkipSettingMaxClocks configure attribute.
    /// <para>获取或设置 configure 的 SkipSettingMaxClocks 属性。</para>
    /// </summary>
    /// <value>Non-zero skips setting maximum clocks; defaults to 0. <para>非零则跳过设置最高时钟；默认 0。</para></value>
    public int SkipSettingMaxClocks { get; set; } = 0;

    /// <summary>
    /// Gets or sets the TargetName configure attribute (optional).
    /// <para>获取或设置 configure 的 TargetName 属性（可选）。</para>
    /// </summary>
    /// <value>The target name sent in configure, or <see langword="null"/> to omit it. <para>configure 中发送的目标名称；为 <see langword="null"/> 时省略。</para></value>
    public string? TargetName { get; set; }

    /// <summary>
    /// Gets or sets the StorageSlot configure attribute (0 = not emitted).
    /// <para>获取或设置 configure 的 StorageSlot 属性（0 表示不输出）。</para>
    /// </summary>
    /// <value>The physical storage slot for configure; 0 omits the attribute. <para>configure 使用的物理存储槽位；0 表示省略该属性。</para></value>
    public int StorageSlot { get; set; } = 0;

    /// <summary>
    /// Gets or sets the retry interval between configure attempts in milliseconds.
    /// <para>获取或设置 configure 尝试之间的重试间隔（毫秒）。</para>
    /// </summary>
    /// <value>The delay before each retry after the first; defaults to 1000. <para>首个尝试之后每次重试前的延迟；默认 1000。</para></value>
    public int ConfigureRetryIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the per-attempt response timeout for configure in milliseconds.
    /// <para>获取或设置 configure 单次尝试的响应超时（毫秒）。</para>
    /// </summary>
    /// <value>The timeout applied to non-first attempts; 0 disables the per-attempt limit. <para>应用于非首次尝试的超时；0 表示禁用单次尝试限制。</para></value>
    public int ConfigureResponseTimeoutMs { get; set; } = EdlConstants.ConfigureResponseTimeoutMs;

    /// <summary>
    /// Gets or sets the response timeout for the FIRST configure attempt in milliseconds.
    /// Right after the Sahara handshake the device may still be booting Firehose and ignore
    /// the first configure; a short timeout lets the retry kick in fast.
    /// <para>获取或设置首次 configure 尝试的响应超时（毫秒）。Sahara 握手刚结束时设备可能
    /// 仍在启动 Firehose 并忽略首个 configure；短超时让重试快速生效。</para>
    /// </summary>
    /// <value>The shortened timeout for attempt zero; 0 falls back to the standard attempt timeout. <para>用于第零次尝试的缩短超时；0 表示回退到标准单次尝试超时。</para></value>
    public int ConfigureFirstAttemptTimeoutMs { get; set; } = EdlConstants.ConfigureFirstAttemptTimeoutMs;

    /// <summary>
    /// Gets or sets the budget in milliseconds for observing transport disconnection after
    /// a power ACK (0 disables observation). Disconnection does not prove successful boot.
    /// <para>获取或设置在 power ACK 之后观察传输断开的预算时间（毫秒），0 表示禁用观察。断开不能证明启动成功。</para>
    /// </summary>
    /// <value>The disconnection-observation window in milliseconds; 0 skips the wait. <para>断开观察窗口（毫秒）；0 表示跳过等待。</para></value>
    public int PowerResetVerifyTimeoutMs { get; set; } = EdlConstants.PowerResetVerifyTimeoutMs;

    /// <summary>
    /// Gets or sets the timeout in milliseconds for the power command's actual response.
    /// <para>获取或设置 power 命令实际响应的超时时间（毫秒）。</para>
    /// </summary>
    /// <value>The response timeout in milliseconds for a power command. <para>power 命令的响应超时（毫秒）。</para></value>
    public int PowerResponseTimeoutMs { get; set; } = EdlConstants.PowerResponseTimeoutMs;

    /// <summary>
    /// Gets or sets an explicit write (ZLP) timeout in milliseconds (0 = per-storage default).
    /// <para>获取或设置显式写入（ZLP）超时（毫秒）（0 = 按存储类型默认值）。</para>
    /// </summary>
    /// <value>The override write timeout in milliseconds; 0 selects the storage-type default. <para>写入超时的显式覆盖值（毫秒）；0 表示选择按存储类型的默认值。</para></value>
    public int WriteTimeoutMs { get; set; } = 0;

    /// <summary>
    /// Gets or sets an explicit erase timeout in milliseconds (0 = per-storage default).
    /// <para>获取或设置显式擦除超时（毫秒）（0 = 按存储类型默认值）。</para>
    /// </summary>
    /// <value>The override erase timeout in milliseconds; 0 selects the storage-type default. <para>擦除超时的显式覆盖值（毫秒）；0 表示选择按存储类型的默认值。</para></value>
    public int EraseTimeoutMs { get; set; } = 0;

    /// <summary>
    /// Converts the storage type to its Firehose XML string.
    /// <para>转换存储类型为 Firehose XML 字符串。</para>
    /// </summary>
    /// <value>The configure <c>STORAGE_TYPE</c> token: <c>ufs</c>, <c>nand</c>, <c>spinor</c>, <c>eMMC</c>, or an empty string for <see cref="FirehoseStorageType.Unknown"/>.
    /// <para>configure 的 <c>STORAGE_TYPE</c> 记号：<c>ufs</c>、<c>nand</c>、<c>spinor</c>、<c>eMMC</c>；<see cref="FirehoseStorageType.Unknown"/> 时为空字符串。</para></value>
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

/// <summary>Implements the Firehose protocol: configure, program, erase, read, memory, power and UFS provisioning operations.
/// <para>实现 Firehose 协议：配置、编程、擦除、读取、内存、电源与 UFS provisioning 操作。</para>Operations on one instance are serialized through their final response. Do not modify the configuration, share the transport with another protocol instance, or synchronously wait for another operation from a log callback while an operation is in progress.</summary>
/// <remarks>Initializes a new instance of the <see cref="FirehoseProtocol"/> class.
/// <para>初始化 <see cref="FirehoseProtocol"/> 类的新实例。</para></remarks>
/// <param name="transport">The device transport.<para>设备传输。</para></param>
/// <param name="config">Optional Firehose configuration.<para>可选的 Firehose 配置。</para></param>
/// <param name="timeoutMs">Optional per-read command timeout in milliseconds.
/// <para>可选的单次读取命令超时（毫秒）。</para></param>
public sealed partial class FirehoseProtocol(ICommTransport transport, FirehoseConfiguration? config = null, int? timeoutMs = null) : IDisposable
{
    private const int DisposalGateTimeoutMs = 250;

    /// <summary>
    /// Per-round bound for the capability-discovery <c>&lt;nop&gt;</c>; expiry abandons discovery and
    /// keeps the gating exactly as strict as before, so one probe never stalls a flash for minutes.
    /// <para>能力发现用的单次 <c>&lt;nop&gt;</c> 往返时限；到点即放弃并保持原有严格门控，
    /// 绝不让一次发现把刷写拖成分钟级等待。</para>
    /// </summary>
    private const int CapabilityDiscoveryTimeoutMs = 5000;

    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _cmdTimeoutMs = timeoutMs ?? EdlConstants.DefaultCommandTimeoutMs;
    private readonly byte[] _rxBuffer = new byte[4 * 1024];
    private readonly byte[] _responseDocument = GC.AllocateUninitializedArray<byte>(MaximumResponseBytes);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _rxBufferBytes;
    private volatile bool _disposed;
    private volatile bool _operationGateRetired;
    private volatile bool _faulted;
    private volatile EdlFailureKind _lastFailureKind;
    private FirehoseCapabilitySnapshot? _capabilitySnapshot;
    private FirehoseStorageGeometry? _lastGeometry;
    private FirehoseStorageDetails? _lastStorageDetails;
    private FirehoseResponse? _lastConfigureAck;
    private IReadOnlySet<string>? _supportedFunctions;
    private bool _capabilityDiscoveryAttempted;

    /// <summary>
    /// Gets the immutable capability snapshot negotiated by the latest successful configure, or
    /// <see langword="null"/> before the first configure completes.
    /// <para>获取最近一次成功 configure 协商得到的不可变能力快照；首次 configure 完成前为
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <value>The snapshot guarding high-risk storage commands. <para>守卫高风险存储命令的快照。</para></value>
    public FirehoseCapabilitySnapshot? Capabilities => _capabilitySnapshot;

    /// <summary>
    /// Gets the descriptive storage facts merged from the latest successful
    /// <c>&lt;getstorageinfo&gt;</c> logs, or <see langword="null"/> when none were reported.
    /// <para>获取从最近一次成功的 <c>&lt;getstorageinfo&gt;</c> 日志合并出的描述性存储信息；
    /// 目标未上报时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The merged descriptive facts. <para>合并后的描述性信息。</para></value>
    public FirehoseStorageDetails? LastStorageDetails => _lastStorageDetails;

    /// <summary>
    /// Gets the typed classification of the most recent failure observed by this protocol instance.
    /// <para>获取本协议实例最近一次观测到的失败的类型化分类。</para>
    /// </summary>
    /// <value>The failure kind, or <see cref="EdlFailureKind.None"/> when no failure was observed.
    /// <para>失败种类；未观测到失败时为 <see cref="EdlFailureKind.None"/>。</para></value>
    public EdlFailureKind LastFailureKind => _lastFailureKind;

    /// <summary>
    /// Gets whether an uncertain transfer or acknowledged power command requires a new device session.
    /// <para>获取不确定的传输或已确认的 power 命令是否需要新的设备会话。</para>
    /// </summary>
    /// <value><see langword="true"/> once the session has faulted; operations on a faulted session throw <see cref="EdlErrorCode.ConnectionLost"/>.
    /// <para>会话进入不可复用状态后为 <see langword="true"/>；之后的操作会抛出 <see cref="EdlErrorCode.ConnectionLost"/>。</para></value>
    public bool RequiresReconnect => _faulted;

    /// <summary>
    /// Gets the current configuration.
    /// <para>获取当前配置。</para>
    /// </summary>
    /// <value>The mutable <see cref="FirehoseConfiguration"/> backing this session; a fresh default instance when none was supplied.
    /// <para>驱动本会话的可变 <see cref="FirehoseConfiguration"/>；未提供时为新建的默认实例。</para></value>
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
    /// <param name="maxRetries">Maximum number of retry attempts. <para>最大重试次数。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    /// <exception cref="EdlException">Thrown when configure fails or the device disconnects. <para>配置失败或设备断开时抛出。</para></exception>
    public async Task<FirehoseResponse> ConfigureAsync(int maxRetries = 30, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ConfigureCoreAsync(maxRetries, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Configures an already-owned transport; the caller must hold the operation lease.</summary>
    internal async Task<FirehoseResponse> ConfigureCoreAsync(int maxRetries, CancellationToken cancellationToken,
        bool deferStorageDetection = false)
    {
        ThrowIfDisposed();
        var attemptedStorageTypes = new HashSet<FirehoseStorageType> { Configuration.StorageType };
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
                    AdoptStorageTypeFromNak(response, attemptedStorageTypes);
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

            AdoptStorageTypeFromAck(response);

            Log($"Configure OK: MaxPayload={Configuration.MaxPayloadSizeToTarget}, FromTarget={Configuration.MaxPayloadSizeFromTarget}, MemoryName={Configuration.StorageType}");
            _lastConfigureAck = response;

            if (!deferStorageDetection)
            {
                await CompleteStorageConfigurationOwnedAsync(cancellationToken).ConfigureAwait(false);
            }

            CaptureCapabilities();
            return response;
        }

        throw new EdlException(EdlErrorCode.ConfigureFailed,
            $"Firehose configure failed after {maxRetries} attempts (timeout).");
    }

    /// <summary>
    /// Adopts the storage type reported by a successful configure acknowledgement so later
    /// storage-dependent defaults and commands follow the device's real media instead of the
    /// pre-configured guess.
    /// <para>采纳成功 configure 应答上报的存储类型，使后续依赖存储类型的默认值与命令跟随设备
    /// 真实介质，而不是沿用配置期的猜测值。</para>
    /// </summary>
    /// <param name="response">The acknowledged configure response. <para>已确认的 configure 应答。</para></param>
    private void AdoptStorageTypeFromAck(FirehoseResponse response)
    {
        var reported = response["MemoryName"];
        if (reported is null || !TryParseStorageType(reported, out var parsed) || parsed == Configuration.StorageType)
        {
            return;
        }

        Log($"Adopting configure ACK MemoryName: {Configuration.StorageType} -> {parsed}.");
        Configuration.StorageType = parsed;
    }

    /// <summary>
    /// Switches the configured storage type when a configure NAK names a different media, so the
    /// next retry sends a matching <c>MemoryName</c>. Each storage type is attempted at most once
    /// per configure call; a NAK that names no new type leaves the retry behaviour unchanged.
    /// <para>当 configure NAK 指明了不同介质时切换所配置的存储类型，使下一次重试发送匹配的
    /// <c>MemoryName</c>。单次 configure 内每种存储类型至多尝试一次；NAK 未指明新类型时重试
    /// 行为保持不变。</para>
    /// </summary>
    /// <param name="response">The rejected configure response. <para>被拒绝的 configure 应答。</para></param>
    /// <param name="attemptedStorageTypes">Storage types already sent in this configure call. <para>本次 configure 已发送过的存储类型。</para></param>
    private void AdoptStorageTypeFromNak(FirehoseResponse response, HashSet<FirehoseStorageType> attemptedStorageTypes)
    {
        var candidates = new[] { response["MemoryName"], response.RawValue, response.RawXml };
        foreach (var candidate in candidates)
        {
            if (candidate is null || !TryMatchStorageTypeText(candidate, out var matched)
                || matched == Configuration.StorageType || !attemptedStorageTypes.Add(matched))
            {
                continue;
            }

            Log($"Configure rejected this storage type; retrying with MemoryName for {matched}.");
            Configuration.StorageType = matched;
            return;
        }
    }

    /// <summary>
    /// Parses a configure <c>MemoryName</c> token into a storage type.
    /// <para>把 configure 的 <c>MemoryName</c> 记号解析为存储类型。</para>
    /// </summary>
    /// <param name="value">The reported memory name. <para>上报的存储名称。</para></param>
    /// <param name="storageType">The parsed storage type on success. <para>解析成功时的存储类型。</para></param>
    /// <returns><see langword="true"/> when the token names a supported storage type. <para>记号指向受支持的存储类型时为 true。</para></returns>
    private static bool TryParseStorageType(string value, out FirehoseStorageType storageType)
    {
        if (string.Equals(value, "ufs", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Ufs;
            return true;
        }

        if (string.Equals(value, "nand", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Nand;
            return true;
        }

        if (string.Equals(value, "spinor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "spi nor", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Spinor;
            return true;
        }

        if (string.Equals(value, "emmc", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Emmc;
            return true;
        }

        storageType = FirehoseStorageType.Unknown;
        return false;
    }

    /// <summary>
    /// Finds a storage-type mention inside a configure rejection message.
    /// <para>在 configure 拒绝消息中查找存储类型提法。</para>
    /// </summary>
    /// <param name="text">The rejection text (attribute, value or raw XML). <para>拒绝文本（属性、值或原始 XML）。</para></param>
    /// <param name="storageType">The first storage type named by the text. <para>文本中首个提及的存储类型。</para></param>
    /// <returns><see langword="true"/> when the text names a supported storage type. <para>文本提及受支持的存储类型时为 true。</para></returns>
    private static bool TryMatchStorageTypeText(string text, out FirehoseStorageType storageType)
    {
        if (text.Contains("ufs", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Ufs;
            return true;
        }

        if (text.Contains("spinor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("spi nor", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Spinor;
            return true;
        }

        if (text.Contains("nand", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Nand;
            return true;
        }

        if (text.Contains("emmc", StringComparison.OrdinalIgnoreCase))
        {
            storageType = FirehoseStorageType.Emmc;
            return true;
        }

        storageType = FirehoseStorageType.Unknown;
        return false;
    }

    internal async Task CompleteStorageConfigurationOwnedAsync(CancellationToken cancellationToken)
    {
        if (Configuration.AutoDetectSectorSize && Configuration.SectorSize == 0)
        {
            await AutoDetectSectorSizeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (Configuration.SectorSize == 0)
        {
            Configuration.SectorSize = EdlConstants.DefaultSectorSizeSmall;
        }

        CaptureCapabilities();
    }

    /// <summary>
    /// Freezes the negotiated capability snapshot from the latest configure acknowledgement.
    /// <para>依据最近的 configure 确认应答定格协商得到的能力快照。</para>
    /// </summary>
    private void CaptureCapabilities()
    {
        if (_lastConfigureAck is { IsAck: true, IsRawMode: false } ack)
        {
            _capabilitySnapshot = FirehoseCapabilitySnapshot.FromConfigureAck(ack, Configuration, _lastGeometry,
                _faulted, DateTimeOffset.UtcNow, _supportedFunctions);
        }
    }

    /// <summary>
    /// Guards one attribute-driven command before any byte reaches the wire. When the configure
    /// acknowledgement simply omitted the attribute, one <c>&lt;nop&gt;</c> round trip asks the
    /// target for its own supported-functions report and the snapshot is renegotiated once; only
    /// after that does the standard refusal apply. Read, program and sparse are governed by their
    /// own negotiation and never spend this round trip.
    /// <para>在任何字节上线之前守护一条由属性驱动的命令。若 configure 应答只是省略了该属性，先用一次
    /// <c>&lt;nop&gt;</c> 往返询问目标自报的支持功能列表并重新定格一次快照，之后才应用标准拒绝。
    /// 读、编程与稀疏由各自协商决定，不消耗这次往返。</para>
    /// </summary>
    /// <param name="command">The command about to be sent. <para>即将发送的命令。</para></param>
    /// <param name="cancellationToken">Cancellation for the discovery round trip.
    /// <para>用于发现往返的取消令牌。</para></param>
    /// <exception cref="EdlException">Thrown when the snapshot reports the capability as unknown or
    /// unsupported. <para>快照报告能力未知或不支持时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled discovery.
    /// <para>调用方取消了发现。</para></exception>
    private async Task RequireDestructiveCapabilityAsync(EdlFirehoseCommand command, CancellationToken cancellationToken)
    {
        if (_capabilitySnapshot?.For(command) is not FeatureSupport.Unknown
            || _capabilityDiscoveryAttempted)
        {
            RequireCapability(command);
            return;
        }

        _capabilityDiscoveryAttempted = true;
        _supportedFunctions = await QuerySupportedFunctionsOwnedAsync(cancellationToken).ConfigureAwait(false);
        if (_supportedFunctions is not null)
        {
            CaptureCapabilities();
        }

        RequireCapability(command);
    }

    /// <summary>
    /// Sends one <c>&lt;nop&gt;</c> and collects the target's supported-functions report. Every
    /// failure — a NAK, a timeout, or an unexpected raw-mode entry — is logged and yields null so
    /// capability gating simply stays as strict as it was; the report only ever upgrades a
    /// capability the acknowledgement already left undeclared.
    /// <para>发送一次 <c>&lt;nop&gt;</c> 并收集目标上报的支持功能列表。任何失败——NAK、超时或意外进入
    /// 原始模式——都记录日志并返回 null，使能力门控保持与此前同样严格；该报告只会提升应答本就
    /// 未声明的能力。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation for the round trip. <para>该次往返的取消令牌。</para></param>
    /// <returns>The reported function names, or null when none could be collected.
    /// <para>上报的功能名；未能收集时为 null。</para></returns>
    private async Task<IReadOnlySet<string>?> QuerySupportedFunctionsOwnedAsync(CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        try
        {
            var response = await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildNop(), cancellationToken,
                responseTimeoutMs: CapabilityDiscoveryTimeoutMs, logObserver: logs.Add).ConfigureAwait(false);
            if (response.IsRawMode)
            {
                _faulted = true;
                Log("Capability discovery unexpectedly entered raw mode; the session now requires a reconnect.");
                return null;
            }

            if (!response.IsAck)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log($"Capability discovery failed: {ex.Message}");
            return null;
        }

        var functions = FirehoseCapabilitySnapshot.ParseSupportedFunctions(logs);
        if (functions is not null)
        {
            Log($"Target reported supported functions: {string.Join(", ", functions.Order())}");
        }

        return functions;
    }

    /// <summary>
    /// Refuses a command when the negotiated snapshot exists and does not report positive support,
    /// so the command's own bytes never reach the wire. Attribute-driven commands may already have
    /// spent one bounded capability-discovery <c>&lt;nop&gt;</c> round trip before this check; an
    /// absent snapshot means the caller drove the low-level protocol without configuring, and
    /// session entry points always configure first.
    /// <para>当已协商的快照存在且未明确报告支持时拒绝该命令，使该命令自身的字节绝不落到线上。
    /// 属性驱动的命令在此检查前可能已经花掉一次有界的能力发现 <c>&lt;nop&gt;</c> 往返；快照缺失表示
    /// 调用方在未 configure 的情况下直接驱动底层协议，而会话入口总是先 configure。</para>
    /// </summary>
    /// <param name="command">The command about to be sent. <para>即将发送的命令。</para></param>
    /// <exception cref="EdlException">Thrown when the snapshot reports the capability as unknown or
    /// unsupported. <para>快照报告能力未知或不支持时抛出。</para></exception>
    private void RequireCapability(EdlFirehoseCommand command)
    {
        var snapshot = _capabilitySnapshot;
        if (snapshot is not null && snapshot.For(command) != FeatureSupport.Supported)
        {
            var kind = snapshot.For(command);
            _lastFailureKind = EdlFailureKind.CapabilityNotReported;
            throw new EdlException(EdlErrorCode.NotSupported,
                $"The target did not report positive support for {command} (state {kind}); the command was refused before its own bytes were sent.")
            {
                FailureKind = EdlFailureKind.CapabilityNotReported
            };
        }
    }

    /// <summary>
    /// Records the typed classification for an observed failure without changing its propagation.
    /// <para>为观测到的失败记录类型化分类，不改变其传播方式。</para>
    /// </summary>
    /// <param name="error">The observed exception. <para>观测到的异常。</para></param>
    /// <param name="transferStarted">Whether raw-mode transfer had begun. <para>原始模式传输是否已开始。</para></param>
    /// <param name="payloadSent">Whether a complete raw payload had already been written. <para>完整原始载荷是否已写入。</para></param>
    private void RecordFailure(Exception error, bool transferStarted = false, bool payloadSent = false)
    {
        var kind = EdlFailureClassification.ClassifyTransfer(error, transferStarted, payloadSent);
        _lastFailureKind = kind;
        if (error is EdlException edl && edl.FailureKind is null)
        {
            edl.FailureKind = kind;
        }
    }

    /// <summary>
    /// Auto-detects the sector size via &lt;getstorageinfo physical_partition_number="0"/&gt;
    /// and stores it in the configuration; falls back to a storage-type default.
    /// <para>通过 &lt;getstorageinfo physical_partition_number="0"/&gt; 自动检测扇区大小并存入
    /// 配置；失败时按存储类型回退默认值。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
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
    /// <param name="blockObserver">Optional bounded collector for structured authentication fields.</param>
    /// <param name="skipResponseAcknowledgement">When non-<see langword="null"/> (mirrors <c>--skipresponse</c>
    /// for loaders that never acknowledge reads or writes), only the command bytes are written and this
    /// synthetic acknowledgement is returned instead of reading any target response. Its raw-mode bit must
    /// already match what the command's data phase expects, and no timeout is shortened.
    /// <para>非 <see langword="null"/> 时（对从不确认读写的 loader 对应 <c>--skipresponse</c>），只写入命令
    /// 字节并返回该合成确认，而不读取任何目标响应。其原始模式位必须已与命令数据阶段所需一致，且不会缩短
    /// 任何超时。</para></param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    private async Task<FirehoseResponse> SendCommandAndWaitResponseAsync(string xml, CancellationToken cancellationToken,
        int? responseTimeoutMs = null, int? writeTimeoutMs = null, Action<string>? logObserver = null,
        Action<FirehoseDataBlock>? blockObserver = null, FirehoseResponse? skipResponseAcknowledgement = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
            await WriteDataAsync(xmlBytes, writeTimeoutMs ?? GetZlpTimeoutMs(), cancellationToken);
            if (skipResponseAcknowledgement is not null)
            {
                return skipResponseAcknowledgement;
            }

            return await ReceiveResponseAsync(cancellationToken, responseTimeoutMs, logObserver, blockObserver);
        }
        catch (Exception ex)
        {
            _faulted = true;
            RecordFailure(ex);
            throw;
        }
    }

    /// <summary>
    /// Builds the synthetic acknowledgement returned when a status-only command is issued under the
    /// <c>--skipresponse</c> contract. The erase command needs no raw mode, so the returned instance keeps
    /// <see cref="FirehoseResponse.IsRawMode"/> clear.
    /// <para>构造在 <c>--skipresponse</c> 约定下发放仅状态命令时返回的合成确认。erase 命令不需要原始模式，
    /// 因此返回实例保持 <see cref="FirehoseResponse.IsRawMode"/> 为假。</para>
    /// </summary>
    /// <returns>A synthetic acknowledged response. <para>一个已确认的合成响应。</para></returns>
    private static FirehoseResponse SkipResponseAcknowledgement(bool rawMode)
        => new() { IsAck = true, RawValue = "ACK", IsRawMode = rawMode };


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
    /// Gets the NAND pages-per-block value carried on program/read/erase commands, or zero on storage that is
    /// not NAND.
    /// <para>获取随 program/read/erase 命令下发的 NAND 每块页数；非 NAND 存储时为零。</para>
    /// </summary>
    private long NandPagesPerBlockHint => Configuration.StorageType == FirehoseStorageType.Nand
        && Configuration.NandPagesPerBlock > 0
            ? Configuration.NandPagesPerBlock
            : 0;

    /// <summary>
    /// Writes data to the transport.
    /// <para>将数据写入传输。</para>
    /// </summary>
    /// <param name="data">The data to write. 要写入的数据。</param>
    /// <param name="timeoutMs">The write timeout in milliseconds. 写入超时（毫秒）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    /// <exception cref="EdlException">Thrown when the transport accepted only part of the payload, because a partial write cannot be replayed.
    /// <para>传输只接受了部分载荷时抛出，因为部分写入无法重放。</para></exception>
    private async Task WriteDataAsync(byte[] data, int timeoutMs, CancellationToken cancellationToken)
    {
        long written;
        try
        {
            written = await _transport.WriteAsync(data, 0, data.Length, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new EdlException(EdlErrorCode.WritePortTimeout, ex.Message, ex);
        }
        if (written != data.Length)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"The transport accepted {written} of {data.Length} bytes; a partial write cannot be replayed.");
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
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return new OperationLease(_operationGate);
        }
        catch
        {
            ReleaseGate(_operationGate);
            throw;
        }
    }

    /// <summary>
    /// Returns one permit to the operation gate, tolerating the window where the owning session was
    /// disposed before the caller finished. A disposed gate has no remaining consumer, so dropping
    /// the release cannot strand a permit or hide a protocol error.
    /// <para>向操作门归还一个许可，并容忍所属会话在调用方结束前已被释放的窗口。已释放的门不再
    /// 有任何使用者，因此丢弃归还不会滞留许可，也不会掩盖协议错误。</para>
    /// </summary>
    private static void ReleaseGate(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Releases one transport transaction. The owning <see cref="FirehoseProtocol"/> disposes the
    /// shared gate, so a lease that outlives the session returns its permit only while the gate is
    /// still usable.
    /// <para>归还一次传输事务。共享门由所属的 <see cref="FirehoseProtocol"/> 释放，因此晚于会话
    /// 结束的租约仅在门仍可用时归还许可。</para>
    /// </summary>
    internal sealed class OperationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _gate, null);
            if (released is not null)
            {
                ReleaseGate(released);
            }
        }
    }

    /// <summary>Marks an uncertain initialization as unusable until the transport is reconnected.</summary>
    internal void Invalidate() => _faulted = true;

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

        ValidateProgramChunkBound();
        var bounded = Math.Min(Configuration.MaxPayloadSizeToTarget, Configuration.MaxProgramChunkBytes);
        if (bounded < Configuration.SectorSize)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                "The effective write chunk is smaller than one sector.");
        }

        return checked((int)(bounded - (bounded % Configuration.SectorSize)));
    }

    /// <summary>
    /// Validates the explicit write-chunk bound before any program byte is produced. The bound must be a
    /// 512-byte multiple inside <c>512..16777216</c> and may not exceed the size the programmer reported
    /// it supports (<see cref="FirehoseConfiguration.MaxPayloadSizeToTargetSupported"/>), so an override
    /// can only widen toward an already-negotiated capacity, never beyond it.
    /// <para>在生成任何编程字节之前校验显式写入块上限。该上限须为 512 的倍数、落在 512 至 16777216
    /// 之间，且不得超过 programmer 报告的支持大小
    /// （<see cref="FirehoseConfiguration.MaxPayloadSizeToTargetSupported"/>），因此覆盖只能朝已协商的
    /// 容量放宽，绝不超出。</para>
    /// </summary>
    private void ValidateProgramChunkBound()
    {
        var bound = Configuration.MaxProgramChunkBytes;
        if (bound is < 512 or > 16777216 || (bound & 511) != 0)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                "The write-chunk bound must be a 512-byte multiple between 512 and 16777216 bytes.");
        }

        if (bound > 1048576 && Configuration.MaxPayloadSizeToTargetSupported > 0
            && bound > Configuration.MaxPayloadSizeToTargetSupported)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                "A raised write-chunk bound may not exceed the payload size the programmer reported as supported.");
        }
    }

    private static void RequireRawMode(FirehoseResponse response, EdlErrorCode errorCode)
    {
        if (!response.IsAck || !response.IsRawMode)
        {
            throw new EdlException(errorCode, "The target did not acknowledge entry into raw mode.");
        }
    }

    /// <summary>
    /// Disposes the protocol and retires the operation gate's wait handle.
    /// <para>释放协议并归还操作门的等待句柄。</para>
    /// </summary>
    /// <remarks>
    /// <para>The gate is taken with a bounded wait rather than by peeking its free-permit count: a holder
    /// blocked inside a long native transfer cannot be outwaited here, and retiring a gate an in-flight
    /// operation still owns would make that operation throw <see cref="ObjectDisposedException"/> while it
    /// returns its permit. When the wait expires the gate is deliberately left alive, trading one wait handle
    /// the garbage collector eventually reclaims against a corrupted transfer. Every later call tries again
    /// until the gate really is retired, so a lease that finishes after this call still lets a repeated
    /// <see cref="Dispose()"/> reclaim the handle.</para>
    /// <para>取闸使用有限等待，而不是查看空闲许可数：阻塞在长原生传输里的持有者不可能在这里等到，而
    /// 处置一个仍被在途操作占用的门会让该操作在归还许可时抛出 <see cref="ObjectDisposedException"/>。
    /// 等待到期时门被刻意保持存活，用一个终会被垃圾回收器收回的等待句柄换取一次不被破坏的传输。
    /// 之后的每次调用都会重新尝试，直到门真正被归还，因此在本次调用之后才结束的租约仍能让重复的
    /// <see cref="Dispose()"/> 回收该句柄。</para>
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
        if (_operationGateRetired)
        {
            return;
        }

        if (!TryEnterOperationGateForDisposal())
        {
            UsbTrace.Log($"FirehoseProtocol.Dispose left the operation gate alive after {DisposalGateTimeoutMs} ms because an operation still holds it.");
            return;
        }

        ReleaseGate(_operationGate);
        _operationGate.Dispose();
        _operationGateRetired = true;
    }

    /// <summary>
    /// Takes the operation gate on behalf of <see cref="Dispose()"/> with a bounded wait, so
    /// disposal can never deadlock behind a transfer whose own timeout is longer than any fixed
    /// wait. An already retired gate, which is what a completed idle disposal leaves behind, counts
    /// as not acquired so repeated disposal stays a safe no-op.
    /// <para>代表 <see cref="Dispose()"/> 以有限等待取得操作门，使释放永远不会被某个自身超时
    /// 长于任何固定等待的传输卡住。已经归还的门（即空闲释放完成后的状态）视为未取得，
    /// 因此重复释放保持安全的空操作。</para>
    /// </summary>
    /// <returns><see langword="true"/> when this call holds the gate.
    /// <para>本次调用取得该门时为 <see langword="true"/>。</para></returns>
    private bool TryEnterOperationGateForDisposal()
    {
        try
        {
            return _operationGate.Wait(DisposalGateTimeoutMs);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}