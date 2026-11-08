using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Represents an EDL (Emergency Download Mode) protocol session combining Sahara and Firehose protocols.
/// <para>表示 EDL（紧急下载模式）协议会话，组合 Sahara 和 Firehose 协议。</para>
/// </summary>
public sealed partial class EdlSession : IProtocolSession
{
    private static readonly TimeSpan MaxRelinkTimeout = TimeSpan.FromHours(24);
    private readonly List<ProtocolEvidence> _initializationEvidence = [];
    private volatile bool _disposed;
    private readonly EdlProbeTransport _probeTransport;
    private EdlProtocolState _protocolState;
    private DeviceEndpoint? _relinkEndpoint;
    private PhysicalDeviceIdentity? _relinkIdentity;
    private TransportGeneration? _relinkGeneration;
    private TimeSpan _relinkTimeout = TimeSpan.FromSeconds(30);
    private EdlRelinkOffer? _lastRelinkOffer;
    private string? _programmerSha256Hex;
    private string? _programmerFileName;
    private bool _programmerHashPreflightDone;

    /// <summary>
    /// Creates a new EDL session over the specified device transport.
    /// <para>在指定设备传输上创建新的 EDL 会话。</para>
    /// </summary>
    /// <param name="transport">The device transport. <para>设备传输。</para></param>
    /// <param name="firehoseConfig">Optional Firehose configuration. <para>可选的 Firehose 配置。</para></param>
    internal EdlSession(ICommTransport transport, FirehoseConfiguration? firehoseConfig = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _probeTransport = new EdlProbeTransport(transport);
        Transport = _probeTransport;
        Firehose = new FirehoseProtocol(Transport, firehoseConfig);
    }

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    /// <value>The constant <c>"edl"</c> identifying the Emergency Download protocol.
    /// <para>常量 <c>"edl"</c>，标识紧急下载协议。</para></value>
    public string ProtocolName => "edl";

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    /// <value>The transport this session exchanges Sahara and Firehose data on.
    /// <para>本会话用于交换 Sahara 与 Firehose 数据的传输。</para></value>
    public ICommTransport Transport { get; }

    /// <summary>
    /// Gets whether the session is connected and ready.
    /// <para>获取会话是否已连接并就绪。</para>
    /// </summary>
    /// <value><c>true</c> while the session is undisposed, the transport is connected, and Firehose
    /// does not require a reconnect. <para>会话未释放、传输已连接且 Firehose 无需重连时为
    /// <c>true</c>。</para></value>
    public bool IsConnected => !_disposed && Transport.IsConnected && Firehose?.RequiresReconnect != true;

    /// <summary>
    /// Gets whether the session reached its configured protocol state, either a Firehose configure or the
    /// streaming DLOAD switch.
    /// <para>获取会话是否已抵达其配置态，即 Firehose 已配置或已切换到流式 DLOAD。</para>
    /// </summary>
    /// <value><c>true</c> after a successful Sahara handshake plus Firehose configure, or after a
    /// command-mode switch to the streaming DLOAD backend; a failed initialization leaves this
    /// <c>false</c>. <para>Sahara 握手与 Firehose 配置均成功，或已按命令模式切换到流式 DLOAD 后端后为
    /// <c>true</c>；初始化失败时保持 <c>false</c>。</para></value>
    public bool IsConfigured { get; private set; }

    /// <summary>Gets the last verified EDL protocol.<para>获取最近一次经校验的 EDL 协议。</para></summary>
    /// <value>The observed protocol, or Unknown when no response was verified.<para>已观测到的协议；未校验到响应时为 Unknown。</para></value>
    public EdlProtocolState ProtocolState => _protocolState;

    /// <summary>Reports verified protocol changes within this session.<para>报告本会话内经校验的协议变化。</para></summary>
    public event EventHandler<EdlProtocolState>? ProtocolStateChanged;

    /// <summary>
    /// Raised after each Sahara READ_DATA/READ_DATA64 block has been served during a programmer upload,
    /// so hosts can render per-image transfer progress.
    /// <para>programmer 上传期间每服务完一个 Sahara READ_DATA/READ_DATA64 块后触发，供宿主呈现逐镜像传输
    /// 进度。</para>
    /// </summary>
    public event EventHandler<SaharaImageProgressEventArgs>? SaharaImageProgress;

    /// <summary>
    /// Gets the ordered evidence produced by programmer selection preflight and initialization gates.
    /// <para>获取 programmer 选择预检与初始化门控产出的按序证据。</para>
    /// </summary>
    /// <value>Append-only across initialization attempts; never contains key material.
    /// <para>跨初始化尝试只增不减；从不包含密钥材料。</para></value>
    public IReadOnlyList<ProtocolEvidence> InitializationEvidence => _initializationEvidence;

    /// <summary>
    /// Gets the Sahara-to-Firehose link stage this session is believed to occupy.
    /// <para>获取本会话被认定所处的 Sahara 到 Firehose 链路阶段。</para>
    /// </summary>
    /// <value>The host-side stage label. <para>宿主侧阶段标签。</para></value>
    public EdlLinkStage LinkStage { get; private set; } = EdlLinkStage.Idle;

    /// <summary>
    /// Gets the latest relink offer published after initialization failed or reconnection became required.
    /// <para>获取初始化失败或需要重连后发布的最近重链提议。</para>
    /// </summary>
    /// <value>The offer, or <see langword="null"/> until a failure occurs with attached endpoint evidence and
    /// after a caller took it with <see cref="TakeRelinkOffer"/>. Reading this property never consumes the offer;
    /// the handover happens exactly once through <see cref="TakeRelinkOffer"/>.
    /// <para>重链提议；在具备已附加端点证据的失败发生前，以及调用方经 <see cref="TakeRelinkOffer"/> 取走之后，
    /// 为 <see langword="null"/>。读取本属性不会消费提议；提议本身由 <see cref="TakeRelinkOffer"/>
    /// 恰好交接一次。</para></value>
    public EdlRelinkOffer? LastRelinkOffer => _lastRelinkOffer;

    /// <summary>
    /// Takes the pending relink offer and clears it, so exactly one caller can act on the published evidence.
    /// <para>取出待处理的重链提议并清空，使恰好一个调用方能够依据已发布的证据行动。</para>
    /// </summary>
    /// <remarks>The handover is atomic: concurrent takers see the offer once, and a later failure publishes a
    /// fresh offer with its deadline measured from that failure. Taking an offer from a session that has already
    /// been disposed is allowed and intentional, because the lease owner disposes the session while the
    /// re-enumeration evidence must still survive into the next operation. The session never re-opens a
    /// transport itself; only the caller holding a device controller can act on the returned offer.</remarks>
    /// <returns>The offer that had not been consumed yet, or <see langword="null"/> when no failure published one
    /// or an earlier caller already took it.
    /// <para>尚未被消费的提议；没有失败发布过提议，或已被更早的调用方取走时为 <see langword="null"/>。</para></returns>
    public EdlRelinkOffer? TakeRelinkOffer()
    {
        EdlRelinkOffer? taken = null;
        return Interlocked.Exchange(ref _lastRelinkOffer, taken);
    }

    /// <summary>
    /// Gets the capability snapshot negotiated by the latest Firehose configure, bound to the
    /// measured programmer identity and the transport generation attached to this session.
    /// <para>获取最近一次 Firehose configure 协商得到的能力快照，并绑定到实测 programmer
    /// 身份与本会话附加的传输代际。</para>
    /// </summary>
    /// <value>
    /// The snapshot once configure has succeeded and the transport is still usable;
    /// <see langword="null"/> before the first successful configure. High-risk storage commands are
    /// refused by the protocol while the required capability is not reported as supported.
    /// <para>configure 成功且传输仍可用时返回快照；首次成功 configure 之前为
    /// <see langword="null"/>。required 能力未被报告为支持时，协议会拒绝高风险存储命令。</para>
    /// </value>
    public FirehoseCapabilitySnapshot? Capabilities
    {
        get
        {
            var snapshot = Firehose?.Capabilities;
            if (snapshot is null)
            {
                return null;
            }

            return _programmerSha256Hex is null && _programmerFileName is null && _relinkGeneration is null
                ? snapshot
                : snapshot with
                {
                    ResourceSha256Hex = _programmerSha256Hex ?? snapshot.ResourceSha256Hex,
                    ProgrammerFileName = _programmerFileName ?? snapshot.ProgrammerFileName,
                    TransportGeneration = _relinkGeneration?.Value ?? snapshot.TransportGeneration
                };
        }
    }

    /// <summary>Attaches the endpoint evidence used to publish relink offers when this link fails.
    /// <para>附加端点证据，用于本链路失败时生成重链提议。</para></summary>
    /// <remarks>The caller supplies the endpoint the session transport was leased on together with the
    /// lease generation; the session never opens or reopens transports itself. Calling this after a
    /// failure means the next failure publishes an offer.</remarks>
    /// <param name="previousEndpoint">The exact endpoint currently backing this session.
    /// <para>当前支撑本会话的精确端点。</para></param>
    /// <param name="generation">The transport generation stamped on the lease, or <see langword="null"/>.
    /// <para>租约盖印的传输代际，或 <see langword="null"/>。</para></param>
    /// <param name="timeout">Recommended relink wait budget between 1 ms and 24 h.
    /// <para>建议的重链等待预算，介于 1 毫秒与 24 小时。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="previousEndpoint"/> is <see langword="null"/>.
    /// <para><paramref name="previousEndpoint"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout leaves the supported range.
    /// <para>截止时间超出支持范围。</para></exception>
    public void AttachRelinkContext(DeviceEndpoint previousEndpoint, TransportGeneration? generation = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(previousEndpoint);
        var effective = timeout ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThan(effective, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effective, MaxRelinkTimeout);
        _relinkEndpoint = previousEndpoint;
        _relinkGeneration = generation;
        _relinkTimeout = effective;
        _relinkIdentity = previousEndpoint.UsbInfo is { } info ? PhysicalDeviceIdentity.From(info) : null;
    }

    /// <summary>
    /// Observes Sahara packets or a Firehose NOP response without loading a programmer or configuring storage.
    /// <para>通过 Sahara 数据包或 Firehose NOP 响应检测协议，不加载 programmer，也不配置存储。</para>
    /// </summary>
    /// <remarks>
    /// A verified state is not re-observed, because probing writes to the target. The one exception is a
    /// Sahara state whose HELLO facts were never captured: a session parked after an earlier probe carries the
    /// verified state but not the handshake facts, and re-running the probe is what re-acquires them. That
    /// probe writes at most one RESET_STATE_MACHINE wake and never a Sahara reply, so repeating it leaves the
    /// target in the same image-transfer state it was already in.
    /// <para>已验证状态不会被重新观测，因为探测会向目标写入。唯一的例外是 Sahara 状态且从未捕获 HELLO 事实：
    /// 早先探测之后暂存的会话携带已验证状态但不携带握手事实，重跑一次探测正是重新获取它们的方式。该探测至多写
    /// 一条 RESET_STATE_MACHINE 唤醒、从不写 Sahara 应答，因此重复它只会让目标停留在原本已处的镜像传输状态。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels bounded protocol reads.<para>取消有界的协议读取。</para></param>
    /// <returns>The verified protocol, or Unknown for absent or incomplete evidence.<para>经校验的协议；证据缺失或不完整时返回 Unknown。</para></returns>
    public async Task<EdlProtocolState> ProbeProtocolAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var needsProbe = _protocolState == EdlProtocolState.Unknown
            || (_protocolState is EdlProtocolState.Sahara or EdlProtocolState.SaharaMemoryDebug
                && SaharaHandshake is null);
        if (needsProbe)
        {
            var state = await _probeTransport.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (state is EdlProtocolState.Sahara or EdlProtocolState.SaharaMemoryDebug
                && _probeTransport.TryReadSaharaHello(out var hello) && hello.Version > 0)
            {
                SaharaHandshake = new SaharaHandshakeInfo(hello.Version, hello.MinVersion,
                    hello.MaxCommandLength, hello.Mode);
            }
            SetProtocolState(state);
        }
        return _protocolState;
    }

    private void SetProtocolState(EdlProtocolState state)
    {
        if (_protocolState != state)
        {
            _protocolState = state;
            ProtocolStateChanged?.Invoke(this, state);
        }
    }

    /// <summary>
    /// Gets the Sahara protocol instance.
    /// <para>获取 Sahara 协议实例。</para>
    /// </summary>
    /// <value>A fresh instance created by each initialization attempt, or <c>null</c> before the
    /// first one starts. <para>每次初始化尝试创建的新实例；首次初始化前为 <c>null</c>。</para></value>
    public SaharaProtocol? Sahara { get; private set; }

    /// <summary>
    /// Gets the passively captured Sahara HELLO facts from the latest probe, without disturbing the
    /// pending handshake.
    /// <para>获取最近一次探测被动捕获的 Sahara HELLO 信息，不干扰未完成的握手。</para>
    /// </summary>
    /// <value>The captured handshake facts, or <see langword="null"/> before a Sahara HELLO was seen.
    /// <para>捕获到的握手信息；尚未观测到 Sahara HELLO 时为 <see langword="null"/>。</para></value>
    public SaharaHandshakeInfo? SaharaHandshake { get; private set; }

    /// <summary>
    /// Gets the COM port name when the endpoint's QDLoader-style serial driver fallback is in use.
    /// <para>端点走 QDLoader 串口驱动回退时，获取对应的 COM 端口名。</para>
    /// </summary>
    /// <value>The serial port name (for example <c>COM3</c>), or <see langword="null"/> when the
    /// session runs over a native USB transport.
    /// <para>串口名（如 <c>COM3</c>）；会话使用原生 USB 传输时为 <see langword="null"/>。</para></value>
    public string? UnderlyingSerialPortName => Transport is EdlProbeTransport { Inner: SerialTransport serial }
        ? serial.PortName
        : null;

    /// <summary>
    /// Gets whether this session runs over the QDLoader serial fallback, whose driver may consume the
    /// bootrom HELLO before the application reads it.
    /// <para>获取本会话是否运行在 QDLoader 串口回退上；该驱动可能在应用读到之前就消费掉 bootrom HELLO。</para>
    /// </summary>
    private bool UsesSerialFallback => Transport is EdlProbeTransport { Inner: SerialTransport };

    /// <summary>
    /// Gets the Firehose protocol instance.
    /// <para>获取 Firehose 协议实例。</para>
    /// </summary>
    /// <value>The instance created with the session; always non-<c>null</c>.
    /// <para>随会话一同创建的实例；始终非 <c>null</c>。</para></value>
    public FirehoseProtocol? Firehose { get; }

    /// <summary>
    /// Gets the Streaming DLOAD protocol instance negotiated through a command-mode Sahara switch.
    /// <para>获取经命令模式 Sahara 切换协商得到的流式 DLOAD 协议实例。</para>
    /// </summary>
    /// <value>The instance once an initialization with <see cref="EdlInitializationOptions.AllowStreamDload"/>
    /// reached the streaming backend, or <see langword="null"/> for Firehose-only sessions and before the
    /// first streaming switch.
    /// <para>当带 <see cref="EdlInitializationOptions.AllowStreamDload"/> 的初始化抵达流式后端后可用；
    /// 仅 Firehose 会话以及首次流式切换之前为 <see langword="null"/>。</para></value>
    public StreamingProtocol? Streaming { get; private set; }

    /// <summary>
    /// Gets which flash backend this session exposes after the most recent successful initialization.
    /// <para>获取最近一次成功初始化后本会话暴露的刷写后端。</para>
    /// </summary>
    /// <value><see cref="EdlDownloadBackend.None"/> before success, <see cref="EdlDownloadBackend.Firehose"/>
    /// for a configured Firehose session, and <see cref="EdlDownloadBackend.StreamDload"/> once the
    /// command-mode Sahara switch has handed the transport to <see cref="Streaming"/>.
    /// <para>成功前为 <see cref="EdlDownloadBackend.None"/>，已配置的 Firehose 会话为
    /// <see cref="EdlDownloadBackend.Firehose"/>，命令模式 Sahara 将传输交接给
    /// <see cref="Streaming"/> 后为 <see cref="EdlDownloadBackend.StreamDload"/>。</para></value>
    public EdlDownloadBackend Backend { get; private set; }

    /// <summary>
    /// Gets the negotiated maximum packet length from Sahara.
    /// <para>从 Sahara 获取协商的最大包长度。</para>
    /// </summary>
    /// <value>The Sahara-negotiated value once a Sahara instance exists, otherwise the 4096-byte
    /// default. <para>Sahara 实例存在后为其协商值，否则为 4096 字节默认值。</para></value>
    public uint MaxPacketLength => Sahara?.MaxPacketLength ?? 4096;

    /// <summary>
    /// Performs Sahara handshake (if needed) and Firehose configure.
    /// <para>执行 Sahara 握手（如需要）和 Firehose 配置。</para>
    /// Concurrent callers share the completed initialization. An initialized session is not
    /// handshaken again; an uncertain failed initialization requires a new session.
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream. <para>可选的编程器镜像流。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    public async Task InitializeAsync(Stream? programmerImage = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await InitializeOwnedAsync(programmerImage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Initializes using explicit Sahara requirements and compatibility settings.
    /// <para>使用显式 Sahara 要求与兼容设置初始化。</para></summary>
    /// <param name="programmerImage">The caller-owned programmer stream.
    /// <para>调用方持有的 programmer 流。</para></param>
    /// <param name="options">The immutable startup requirements.
    /// <para>不可变的启动要求。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task completing after Sahara and Firehose configuration succeed.
    /// <para>在 Sahara 与 Firehose 配置成功后完成的任务。</para></returns>
    public async Task InitializeAsync(Stream? programmerImage, EdlInitializationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await InitializeOwnedAsync(programmerImage, cancellationToken, options).ConfigureAwait(false);
    }

    /// <summary>Initializes with an evidence-backed programmer selection and a mandatory hash preflight.
    /// <para>使用有证据支撑的 programmer 选择结果与强制哈希预检初始化。</para></summary>
    /// <remarks>The selection must be <see cref="EdlProgrammerMatchDecision.Matched"/>with a
    /// descriptor, and the SHA-256 measured from <paramref name="programmerImage"/>must equal both the
    /// descriptor hash and, when recorded, the selection's measured hash. Any violation fails before
    /// the operation gate is entered, so no byte reaches the device. The stream must be readable and
    /// seekable.</remarks>
    /// <param name="selection">The catalog selection produced for this device.
    /// <para>针对该设备生成的目录选择结果。</para></param>
    /// <param name="programmerImage">The caller-owned programmer stream.
    /// <para>调用方持有的 programmer 流。</para></param>
    /// <param name="options">Optional immutable startup requirements.<para>可选的不可变启动要求。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task completing after preflight, Sahara upload and Firehose configuration succeed.
    /// <para>在预检、Sahara 上传与 Firehose 配置均成功后完成的任务。</para></returns>
    /// <exception cref="EdlException">The decision is not a unique match (<see cref="EdlErrorCode.NeedsProgrammerSelection"/>) or the measured hash contradicts the descriptor (<see cref="EdlErrorCode.ProgrammerMismatch"/>).
    /// <para>结论并非唯一匹配（<see cref="EdlErrorCode.NeedsProgrammerSelection"/>），或实测哈希与描述符矛盾 （<see cref="EdlErrorCode.ProgrammerMismatch"/>）。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot be hashed without device interaction.
    /// <para>流无法在不接触设备的情况下完成哈希。</para></exception>
    public async Task InitializeAsync(EdlProgrammerSelection selection, Stream programmerImage,
        EdlInitializationOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(programmerImage);

        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Descriptor is not { } descriptor
            || selection.Decision != EdlProgrammerMatchDecision.Matched)
        {
            RecordInitializationEvidence("programmer-selection", CommandOutcomeKind.Rejected,
                $"Decision {selection.Decision} is not an evidence-backed unique match.",
                selection.Descriptor?.Sha256Hex, null);
            throw new EdlException(selection.Decision == EdlProgrammerMatchDecision.NeedsExplicitSelection
                ? EdlErrorCode.NeedsProgrammerSelection
                : EdlErrorCode.ProgrammerMismatch);
        }

        descriptor.Validate();
        RecordInitializationEvidence("programmer-selection", CommandOutcomeKind.Completed,
            $"Matched {descriptor.FileName}.", descriptor.Sha256Hex, null);
        var measured = MeasureProgrammerHash(programmerImage, cancellationToken);
        var matched = string.Equals(measured, descriptor.Sha256Hex, StringComparison.OrdinalIgnoreCase)
            && (selection.ActualSha256Hex is null
                || string.Equals(measured, selection.ActualSha256Hex, StringComparison.OrdinalIgnoreCase));
        RecordInitializationEvidence("programmer-hash-preflight",
            matched ? CommandOutcomeKind.Completed : CommandOutcomeKind.Rejected,
            matched ? "Measured SHA-256 equals the descriptor identity." : "Measured SHA-256 contradicts the selected descriptor.",
            measured, null);
        if (!matched)
        {
            throw new EdlException(EdlErrorCode.ProgrammerMismatch,
                $"Programmer identity mismatch: expected {descriptor.Sha256Hex}, measured {measured}.");
        }

        _programmerSha256Hex = measured;
        _programmerFileName = descriptor.FileName;
        _programmerHashPreflightDone = true;
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await InitializeOwnedAsync(programmerImage, cancellationToken, options).ConfigureAwait(false);
    }

    private string MeasureProgrammerHash(Stream programmerImage, CancellationToken cancellationToken)
    {
        try
        {
            return EdlProgrammerHash.ComputeSha256Hex(programmerImage, cancellationToken);
        }
        catch (Exception error) when (error is not EdlException)
        {
            RecordInitializationEvidence("programmer-hash-preflight", ClassifyFailure(error),
                "The programmer stream could not be hashed before device interaction.", null, null);
            throw;
        }
    }

    /// <summary>
    /// Observes the endpoint once before startup so a target that is already running Firehose is detected
    /// instead of being handed a Sahara exchange it cannot interpret. The caller already holds this session's
    /// Firehose operation lease.
    /// <para>在启动前观测一次端点，使已在运行 Firehose 的目标被识别出来，而不是收到它无法解析的 Sahara 交换。
    /// 调用方已持有本会话的 Firehose 操作租约。</para>
    /// </summary>
    /// <remarks>
    /// Only an unobserved session is probed; a verified state is never re-observed, because probing writes to the
    /// target. A probe failure leaves the state Unknown and the caller keeps its existing startup path, so this
    /// preflight can only improve the outcome it would have reached anyway.
    /// <para>只有尚未观测过的会话才会被探测；已验证状态绝不重新观测，因为探测会向目标写入。探测失败会保持
    /// Unknown 并让调用方沿用既有启动路径，因此该预检只可能改善原本就会得到的结果。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the bounded probe.<para>取消有界探测。</para></param>
    private async Task ProbeProtocolBeforeInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _probeTransport.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (state is EdlProtocolState.Sahara or EdlProtocolState.SaharaMemoryDebug
                && _probeTransport.TryReadSaharaHello(out var hello) && hello.Version > 0)
            {
                SaharaHandshake = new SaharaHandshakeInfo(hello.Version, hello.MinVersion,
                    hello.MaxCommandLength, hello.Mode);
            }
            SetProtocolState(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommTrace.Debug("edl", $"EDL startup preflight probe failed and the startup path is unchanged: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes while the caller holds this session's Firehose operation lease, answering the
    /// <c>Firehose</c> image id with the single supplied stream.
    /// <para>在调用方持有本会话 Firehose 操作租约时初始化，用所给单个流应答 <c>Firehose</c> 镜像标识。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream, or <see langword="null"/> to start without an upload.
    /// <para>可选的 programmer 镜像流；为 <see langword="null"/> 时不带上传启动。</para></param>
    /// <param name="cancellationToken">Cancels the handshake and configuration.
    /// <para>取消握手与配置。</para></param>
    /// <param name="options">Optional startup requirements.
    /// <para>可选的启动要求。</para></param>
    /// <returns>A task completing when the session reaches its configured state.
    /// <para>会话抵达配置态时完成的任务。</para></returns>
    internal Task InitializeOwnedAsync(Stream? programmerImage, CancellationToken cancellationToken,
        EdlInitializationOptions? options = null)
        => InitializeOwnedFromImagesAsync(
            programmerImage is null
                ? null
                : new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = programmerImage },
            cancellationToken, options);

    /// <summary>Initializes from a set of images keyed by the Sahara image id the target will request, while the
    /// caller holds this session's Firehose operation lease.</summary>
    internal async Task InitializeOwnedFromImagesAsync(IReadOnlyDictionary<uint, Stream>? programmerImages,
        CancellationToken cancellationToken, EdlInitializationOptions? options = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConfigured)
        {
            if (options?.QueryIdentityBeforeUpload == true && Sahara?.DeviceIdentity is null)
            {
                throw new InvalidOperationException("The configured session has no verified Sahara identity report.");
            }
            return;
        }

        var skipResponse = options?.SkipResponseOnReadWrite ?? false;
        Firehose!.Configuration.SkipResponseOnErase = skipResponse;
        Firehose.Configuration.SkipResponseOnReadWrite = skipResponse;
        if (HasProgrammerImages(programmerImages) && !_programmerHashPreflightDone)
        {
            RecordProgrammerHashPreflight(programmerImages!, cancellationToken);
        }
        if (_protocolState == EdlProtocolState.Unknown)
        {
            await ProbeProtocolBeforeInitializationAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var streamDloadSelected = false;
            if (_protocolState == EdlProtocolState.SaharaMemoryDebug)
            {
                throw new SaharaException(
                    "The target reports Sahara memory-debug mode, which offers crashdump regions instead of image " +
                    "transfer. Collect the ramdump or re-enter EDL before uploading a programmer.");
            }
            if (_protocolState != EdlProtocolState.Firehose)
            {
                LinkStage = HasProgrammerImages(programmerImages) ? EdlLinkStage.ProgrammerUploading : EdlLinkStage.SaharaConnecting;
                if (Sahara is { } previousSahara)
                {
                    previousSahara.ImageProgress -= ForwardSaharaImageProgress;
                }

                Sahara = new SaharaProtocol(Transport)
                {
                    QudHelloRecoveryEnabled = options?.QudHelloRecoveryEnabled ?? UsesSerialFallback,
                    AllowSingleFirehosePendingCompletion = options?.AllowSingleFirehosePendingCompletion ?? false,
                    QueryIdentityBeforeUpload = options?.QueryIdentityBeforeUpload ?? false,
                    AllowStreamDload = options?.AllowStreamDload ?? false
                };
                Sahara.ImageProgress += ForwardSaharaImageProgress;
                var mode = HasProgrammerImages(programmerImages)
                    ? await Sahara.HandshakeAsync(programmerImages!, cancellationToken).ConfigureAwait(false)
                    : await Sahara.HandshakeAsync(programmerImage: null, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (mode == SaharaMode.Command && options?.AllowStreamDload == true && !HasProgrammerImages(programmerImages))
                {
                    await EnterStreamDloadAsync(cancellationToken).ConfigureAwait(false);
                    streamDloadSelected = true;
                    Backend = EdlDownloadBackend.StreamDload;
                    SetProtocolState(EdlProtocolState.Streaming);
                    IsConfigured = true;
                    RecordInitializationEvidence("stream-dload-selected", CommandOutcomeKind.Completed,
                        "The command-mode Sahara target switched to the streaming DLOAD backend.", null, null);
                }
                else
                {
                    SetProtocolState(EdlProtocolState.Firehose);
                    LinkStage = HasProgrammerImages(programmerImages)
                        ? EdlLinkStage.AwaitingReenumeration
                        : EdlLinkStage.FirehoseVerifying;
                }
            }
            else if (options?.QueryIdentityBeforeUpload == true)
            {
                throw new SaharaException("A Firehose-only connection cannot provide the required Sahara identity.");
            }

            if (!streamDloadSelected)
            {
                LinkStage = EdlLinkStage.FirehoseVerifying;
                await Firehose!.ConfigureCoreAsync(30, cancellationToken, deferStorageDetection: true).ConfigureAwait(false);
                await Firehose.CompleteStorageConfigurationOwnedAsync(cancellationToken).ConfigureAwait(false);
                IsConfigured = true;
                LinkStage = EdlLinkStage.FirehoseReady;
                Backend = EdlDownloadBackend.Firehose;
            }
        }
        catch
        {
            Streaming?.Dispose();
            Streaming = null;
            Backend = EdlDownloadBackend.None;
            SetProtocolState(EdlProtocolState.Unknown);
            Firehose!.Invalidate();
            LinkStage = EdlLinkStage.Failed;
            PublishRelinkOffer();
            throw;
        }
    }

    /// <summary>Forwards the active Sahara instance's image progress to this session's subscribers.
    /// <para>把当前 Sahara 实例的镜像进度转发给本会话的订阅者。</para></summary>
    /// <param name="sender">The Sahara protocol instance. <para>Sahara 协议实例。</para></param>
    /// <param name="e">The per-block progress. <para>逐块进度。</para></param>
    private void ForwardSaharaImageProgress(object? sender, SaharaImageProgressEventArgs e)
        => SaharaImageProgress?.Invoke(this, e);

    /// <summary>
    /// Measures the SHA-256 of every programmer image before any device interaction and records it as
    /// initialization evidence; a single-image set also becomes the session's programmer identity so
    /// capability snapshots and bundle results carry the measured upload identity.
    /// <para>在任何设备交互之前测量每个 programmer 镜像的 SHA-256 并记录为初始化证据；单镜像集合同时成为
    /// 会话的 programmer 身份，使能力快照与刷写包结果携带实测的上传身份。</para>
    /// </summary>
    /// <remarks>The measurement reuses the streaming hash and restores each stream position, so the
    /// handshake uploads the identical bytes afterwards.<para>测量复用流式哈希并恢复每个流的位置，因此
    /// 握手随后上传的正是相同字节。</para></remarks>
    /// <param name="programmerImages">The non-empty image set about to be uploaded.
    /// <para>即将上传的非空镜像集合。</para></param>
    /// <param name="cancellationToken">Cancellation checked between hash chunks.
    /// <para>在哈希数据块之间检查的取消令牌。</para></param>
    private void RecordProgrammerHashPreflight(IReadOnlyDictionary<uint, Stream> programmerImages,
        CancellationToken cancellationToken)
    {
        try
        {
            string? singleImageHash = null;
            foreach (var (imageId, stream) in programmerImages)
            {
                var measured = EdlProgrammerHash.ComputeSha256Hex(stream, cancellationToken);
                RecordInitializationEvidence("programmer-hash-preflight", CommandOutcomeKind.Completed,
                    $"Image {imageId}: measured SHA-256 {measured} before device interaction.", measured, null);
                singleImageHash = measured;
            }

            if (programmerImages.Count == 1)
            {
                _programmerSha256Hex = singleImageHash;
            }

            _programmerHashPreflightDone = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordInitializationEvidence("programmer-hash-preflight", ClassifyFailure(error),
                "The programmer stream could not be hashed before device interaction.", null, null);
            throw;
        }
    }

    /// <summary>Drives the command-mode Sahara exec switch and negotiates the streaming DLOAD hello.
    /// <para>驱动命令模式 Sahara 的 exec 切换并协商流式 DLOAD 的 hello。</para></summary>
    /// <remarks>The caller holds the Firehose operation lease so the transport is exclusive. A failed
    /// switch leaves <see cref="Streaming"/> unassigned and lets the caller's cleanup dispose any
    /// partially negotiated protocol.</remarks>
    /// <param name="cancellationToken">Cancellation token threaded through the exec and hello exchanges.
    /// <para>贯穿 exec 与 hello 交换的取消令牌。</para></param>
    private async Task EnterStreamDloadAsync(CancellationToken cancellationToken)
    {
        await Sahara!.SwitchToStreamDloadAsync(cancellationToken).ConfigureAwait(false);
        var streaming = new StreamingProtocol(Transport);
        await streaming.HelloAsync(cancellationToken).ConfigureAwait(false);
        Streaming = streaming;
    }

    /// <summary>Classifies a failed exchange into the shared evidence outcome vocabulary.
    /// <para>将失败的交换归类到共享证据结果词汇。</para></summary>
    private static CommandOutcomeKind ClassifyFailure(Exception error) => error switch
    {
        TimeoutException => CommandOutcomeKind.Timeout,
        OperationCanceledException => CommandOutcomeKind.Cancelled,
        EdlException { ErrorCode: EdlErrorCode.ReadPortTimeout or EdlErrorCode.WritePortTimeout or EdlErrorCode.TransportTimeout }
            => CommandOutcomeKind.Timeout,
        EdlException { ErrorCode: EdlErrorCode.ConnectionLost } => CommandOutcomeKind.ConnectionLost,
        EdlException => CommandOutcomeKind.Rejected,
        _ => CommandOutcomeKind.Rejected
    };

    private void RecordInitializationEvidence(string phase, CommandOutcomeKind outcome, string? detail,
        string? resourceSha256Hex, long? elapsedMs)
    {
        _initializationEvidence.Add(new ProtocolEvidence
        {
            Protocol = "edl",
            Phase = phase,
            Outcome = outcome,
            Detail = detail,
            ResourceSha256Hex = resourceSha256Hex,
            ElapsedMs = elapsedMs
        });
    }

    /// <summary>Publishes a relink offer when endpoint evidence was attached before the failure.
    /// <para>当失败前附加了端点证据时发布重链提议。</para></summary>
    /// <remarks>The published offer replaces any earlier one a caller has not taken yet, so a consumer that
    /// never takes an offer cannot accumulate them; each failure republishes with a deadline measured from
    /// that failure rather than from the first.
    /// <para>发布的提议会替换调用方尚未取走的更早提议，因此从不取走提议的消费者不会累积提议；
    /// 每次失败都重新发布，截止时间按该次失败而非首次失败计算。</para></remarks>
    private void PublishRelinkOffer()
    {
        if (_relinkEndpoint is not { } endpoint)
        {
            return;
        }

        Interlocked.Exchange(ref _lastRelinkOffer, new EdlRelinkOffer
        {
            PreviousEndpoint = endpoint,
            Identity = _relinkIdentity,
            ObservedGeneration = _relinkGeneration,
            LastIdentity = Sahara?.DeviceIdentity,
            Timeout = _relinkTimeout,
            Correlation = CorrelationEvidence(_relinkIdentity)
        });
    }

    private static ModeTransitionEvidence? CorrelationEvidence(PhysicalDeviceIdentity? identity) => identity switch
    {
        null => null,
        { ContainerId: { } container } when container != Guid.Empty => ModeTransitionEvidence.Container,
        { PhysicalPortPath: { Length: > 0 and <= 1024 } port } when !port.Any(char.IsWhiteSpace)
            && !port.Any(char.IsControl) => ModeTransitionEvidence.PhysicalPort,
        _ => null
    };

    /// <summary>
    /// Ensures the session is initialized (handshake + configure) without rebuilding an already configured
    /// session or a link that was handed to the streaming DLOAD backend, whose Sahara exchange can happen only
    /// once per link.
    /// <para>确保会话已初始化（握手 + 配置），不重建已配置的会话或已交接给流式 DLOAD 后端的链路，因为
    /// 后者的 Sahara 交换在一条链路上只能发生一次。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream. <para>可选的编程器镜像流。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    public async Task EnsureInitializedAsync(Stream? programmerImage = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConfigured || _protocolState == EdlProtocolState.Streaming)
        {
            return;
        }

        await InitializeAsync(programmerImage, cancellationToken);
    }

    /// <summary>
    /// Ensures the session is initialized with explicit startup requirements, or returns immediately for an
    /// already configured session or a link that was handed to the streaming DLOAD backend.
    /// <para>确保会话按显式启动要求初始化；对已配置的会话或已交接给流式 DLOAD 后端的链路直接返回。</para>
    /// </summary>
    /// <remarks>Startup requirements are validated even when the session is already configured, so a late request
    /// for configuration options fails loudly instead of being skipped. A streaming DLOAD link is never
    /// re-handshaken, because its Sahara exchange can happen only once.</remarks>
    /// <param name="programmerImage">Optional programmer image stream. <para>可选的编程器镜像流。</para></param>
    /// <param name="options">Immutable startup requirements, or <see langword="null"/> for the defaults.
    /// <para>不可变的启动要求；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    public async Task EnsureInitializedAsync(Stream? programmerImage, EdlInitializationOptions? options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (options is not null)
        {
            await InitializeAsync(programmerImage, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsConfigured || _protocolState == EdlProtocolState.Streaming)
        {
            return;
        }

        await InitializeAsync(programmerImage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether a programmer input set carries at least one image, which is the condition the Sahara
    /// exchange needs before it can answer READ_DATA at all.
    /// <para>报告 programmer 输入集合是否至少含一个镜像，这是 Sahara 交换应答 READ_DATA 的前提条件。</para>
    /// </summary>
    /// <param name="programmerImages">Candidate image set, possibly <see langword="null"/>.
    /// <para>候选镜像集合，可能为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when at least one image is present.
    /// <para>至少存在一个镜像时为 <see langword="true"/>。</para></returns>
    private static bool HasProgrammerImages(IReadOnlyDictionary<uint, Stream>? programmerImages)
        => programmerImages is { Count: > 0 };

    /// <summary>
    /// Opens every image of a programmer set, uploads all of them in one Sahara exchange, and disposes them again.
    /// <para>打开 programmer 集合的全部镜像，在一次 Sahara 交换中上传它们，随后释放。</para>
    /// </summary>
    /// <remarks>
    /// The caller holds the Firehose operation lease, so the transport is exclusive. Streams already opened are
    /// disposed even when a later image cannot be opened, so a partial open never leaks a file handle.
    /// <para>调用方持有 Firehose 操作租约，因此传输独占。即使后续镜像无法打开，已打开的流也会被释放，
    /// 部分打开不会泄漏文件句柄。</para>
    /// </remarks>
    /// <param name="programmerImages">The validated image set to upload.
    /// <para>要上传的已校验镜像集合。</para></param>
    /// <param name="cancellationToken">Cancels the opens and the handshake.
    /// <para>取消打开与握手。</para></param>
    /// <param name="options">Optional startup requirements. <para>可选的启动要求。</para></param>
    /// <returns>A task completing when the session reaches its configured state.
    /// <para>会话抵达配置态时完成的任务。</para></returns>
    /// <exception cref="InvalidDataException">One referenced image is missing.
    /// <para>某个被引用的镜像缺失。</para></exception>
    /// <exception cref="IOException">One referenced image cannot be opened.
    /// <para>某个被引用的镜像无法打开。</para></exception>
    private async Task InitializeOwnedFromImageSetAsync(EdlProgrammerImageSet programmerImages,
        CancellationToken cancellationToken, EdlInitializationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(programmerImages);
        var streams = new Dictionary<uint, Stream>(programmerImages.Images.Count);
        try
        {
            foreach (var image in programmerImages.Images)
            {
                if (!File.Exists(image.Path))
                {
                    throw new InvalidDataException(
                        $"The programmer image is missing: {image.Path}.");
                }

                streams[image.ImageId] = File.Open(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            await InitializeOwnedFromImagesAsync(streams, cancellationToken, options).ConfigureAwait(false);
        }
        finally
        {
            foreach (var stream in streams.Values)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Initializes the session from a programmer image set, so a manifest that spreads the programmer across
    /// several Sahara image identifiers uploads all of them in one exchange.
    /// <para>用 programmer 镜像集合初始化会话，使把 programmer 分散到多个 Sahara 镜像标识上的清单在同一次交换中
    /// 上传全部镜像。</para>
    /// </summary>
    /// <param name="programmerImages">The validated image set to upload.
    /// <para>要上传的已校验镜像集合。</para></param>
    /// <param name="initialization">Optional startup requirements.
    /// <para>可选的启动要求。</para></param>
    /// <param name="cancellationToken">Cancels the opens, the handshake and the configuration.
    /// <para>取消打开、握手与配置。</para></param>
    /// <returns>A task completing when the session reaches its configured state.
    /// <para>会话抵达配置态时完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="programmerImages"/> is <see langword="null"/>.
    /// <para><paramref name="programmerImages"/> 为 <see langword="null"/>。</para></exception>
    public async Task InitializeFromImageSetAsync(EdlProgrammerImageSet programmerImages,
        EdlInitializationOptions? initialization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(programmerImages);
        ThrowIfDisposed();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await InitializeOwnedFromImageSetAsync(programmerImages, cancellationToken, initialization)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the session is initialized from a programmer image set, returning immediately for an already
    /// configured session or a link handed to the streaming DLOAD backend.
    /// <para>确保会话用 programmer 镜像集合初始化；对已配置的会话或已交接给流式 DLOAD 后端的链路直接返回。</para>
    /// </summary>
    /// <remarks>The early return happens before any image is opened, so an already configured endpoint pays no
    /// extra file I/O.<para>提前返回发生在任何镜像被打开之前，因此已配置的端点不产生额外文件 I/O。</para></remarks>
    /// <param name="programmerImages">The validated image set to upload.
    /// <para>要上传的已校验镜像集合。</para></param>
    /// <param name="initialization">Optional startup requirements.
    /// <para>可选的启动要求。</para></param>
    /// <param name="cancellationToken">Cancels the opens, the handshake and the configuration.
    /// <para>取消打开、握手与配置。</para></param>
    /// <returns>A task completing when the session reaches its configured state.
    /// <para>会话抵达配置态时完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="programmerImages"/> is <see langword="null"/>.
    /// <para><paramref name="programmerImages"/> 为 <see langword="null"/>。</para></exception>
    public async Task EnsureInitializedFromImageSetAsync(EdlProgrammerImageSet programmerImages,
        EdlInitializationOptions? initialization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(programmerImages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConfigured || _protocolState == EdlProtocolState.Streaming)
        {
            return;
        }

        await InitializeFromImageSetAsync(programmerImages, initialization, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Programs (flashes) data to a partition.
    /// <para>将数据编程（刷写）到分区。</para>
    /// </summary>
    /// <param name="dataStream">The data stream to write. <para>要写入的数据流。</para></param>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> FlashPartitionAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await Firehose!.ProgramAsync(dataStream, startSector, numSectors, filename, physicalPartition, cancellationToken);
    }

    /// <summary>
    /// Erases a partition.
    /// <para>擦除分区。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> ErasePartitionAsync(long startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await Firehose!.EraseAsync(startSector, numSectors, physicalPartition, cancellationToken);
    }

    /// <summary>
    /// Reads data from a partition.
    /// <para>从分区读取数据。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. <para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors. <para>扇区数量。</para></param>
    /// <param name="filename">The target filename. <para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A tuple containing the response and data. <para>包含响应和数据的元组。</para></returns>
    public async Task<(FirehoseResponse Response, byte[] Data)> ReadPartitionAsync(long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await Firehose!.ReadAsync(startSector, numSectors, filename, physicalPartition, cancellationToken);
    }

    /// <summary>Streams a partition range into a caller-owned destination without allocating a destination-sized buffer, hashing every transferred byte and reporting cumulative progress.
    /// <para>将分区范围流式写入调用方持有的目标，不分配等同目标大小的缓冲，对每个传输字节计算 哈希并上报累计进度。</para></summary>
    /// <remarks>Unlike <see cref="ReadPartitionAsync"/>, this path is not bounded by the 16 MiB in-memory limit. Atomic publication is a host responsibility: publish by writing to a temporary file and renaming it only after this call returns; a cancelled or failed read leaves the destination partially written and must never be published as a completed backup. Short reads and hash mismatches throw before a result is produced.
    /// <para>与 <see cref="ReadPartitionAsync"/> 不同，本路径不受 16 MiB 内存上限约束。原子发布 是宿主职责：先写临时文件，只在本调用返回后重命名发布；被取消或失败的读取会留下部分写入的 目标，绝不能作为完成备份发布。短读与哈希不符会在产出结果前抛出。</para></remarks>
    /// <param name="destination">Writable, caller-owned destination stream kept open for the whole call.
    /// <para>可写、由调用方持有并在整个调用期间保持打开的目标流。</para></param>
    /// <param name="startSector">The starting sector.<para>起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors.<para>扇区数量。</para></param>
    /// <param name="filename">The target filename.<para>目标文件名。</para></param>
    /// <param name="physicalPartition">The physical partition number.<para>物理分区编号。</para></param>
    /// <param name="progress">Optional cumulative transferred bytes; not evidence of completion.
    /// <para>可选的累计传输字节；不是完成证据。</para></param>
    /// <param name="expectedSha256Hex">Optional lowercase hex SHA-256 the transferred bytes must match.
    /// <para>可选的十六进制小写 SHA-256；传输字节必须匹配。</para></param>
    /// <param name="cancellationToken">Cancellation token threaded through transport and destination writes.
    /// <para>贯穿传输与目标写入的取消令牌。</para></param>
    /// <returns>The measured result with length, hash, and the device acknowledgement.
    /// <para>带长度、哈希与设备确认的计量结果。</para></returns>
    /// <exception cref="EdlException">Thrown for short reads, missing completion acknowledgements, or hash contradictions; the partially written destination is left unpublished.
    /// <para>短读、缺失完成确认或哈希矛盾时抛出；部分写入的目标保持未发布。</para></exception>
    public async Task<EdlStreamReadResult> ReadPartitionToStreamAsync(Stream destination, long startSector, long numSectors,
        string filename = "partition.bin", int physicalPartition = 0, IProgress<long>? progress = null,
        string? expectedSha256Hex = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await Firehose!.ReadPartitionToStreamAsync(destination, startSector, numSectors, filename,
            physicalPartition, progress, expectedSha256Hex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets the device.
    /// <para>复位设备。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await Firehose!.ResetAsync(cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> when the session has been disposed, or an
    /// <see cref="EdlException"/> reporting the lost connection once reconnection is required.
    /// <para>会话已释放时抛出 <see cref="ObjectDisposedException"/>；需要重连后抛出报告连接丢失的
    /// <see cref="EdlException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EdlSession));
        }

        if (Firehose?.RequiresReconnect == true)
        {
            LinkStage = EdlLinkStage.Stale;
            PublishRelinkOffer();
            throw new EdlException(EdlErrorCode.ConnectionLost, "The EDL session requires reconnection.");
        }
    }

    /// <summary>
    /// Disposes the session and releases all resources.
    /// <para>释放会话并释放所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Sahara?.Dispose();
        Streaming?.Dispose();
        Firehose?.Dispose();
        Transport.Dispose();
    }
}