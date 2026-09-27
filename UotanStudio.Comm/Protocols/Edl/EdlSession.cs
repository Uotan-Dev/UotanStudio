using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Represents an EDL (Emergency Download Mode) protocol session combining Sahara and Firehose protocols.
/// <para>表示 EDL（紧急下载模式）协议会话，组合 Sahara 和 Firehose 协议。</para>
/// </summary>
public sealed class EdlSession : IProtocolSession
{
    private volatile bool _disposed;
    private readonly EdlProbeTransport _probeTransport;
    private EdlProtocolState _protocolState;

    /// <summary>
    /// Creates a new EDL session over the specified device transport.
    /// <para>在指定设备传输上创建新的 EDL 会话。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="firehoseConfig">Optional Firehose configuration. 可选的 Firehose 配置。</param>
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
    /// Gets whether Firehose has been configured.
    /// <para>Firehose 是否已配置。</para>
    /// </summary>
    /// <value><c>true</c> after a successful Sahara handshake plus Firehose configure; a failed
    /// initialization leaves this <c>false</c>. <para>Sahara 握手与 Firehose 配置均成功后为
    /// <c>true</c>；初始化失败时保持 <c>false</c>。</para></value>
    public bool IsConfigured { get; private set; }

    /// <summary>Gets the last verified EDL protocol.<para>获取最近一次经校验的 EDL 协议。</para></summary>
    /// <value>The observed protocol, or Unknown when no response was verified.<para>已观测到的协议；未校验到响应时为 Unknown。</para></value>
    public EdlProtocolState ProtocolState => _protocolState;

    /// <summary>Reports verified protocol changes within this session.<para>报告本会话内经校验的协议变化。</para></summary>
    public event EventHandler<EdlProtocolState>? ProtocolStateChanged;

    /// <summary>
    /// Observes Sahara packets or a Firehose NOP response without loading a programmer or configuring storage.
    /// <para>通过 Sahara 数据包或 Firehose NOP 响应检测协议，不加载 programmer，也不配置存储。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels bounded protocol reads.<para>取消有界的协议读取。</para></param>
    /// <returns>The verified protocol, or Unknown for absent or incomplete evidence.<para>经校验的协议；证据缺失或不完整时返回 Unknown。</para></returns>
    public async Task<EdlProtocolState> ProbeProtocolAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_protocolState == EdlProtocolState.Unknown)
        {
            SetProtocolState(await _probeTransport.ProbeAsync(cancellationToken).ConfigureAwait(false));
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
    /// Gets the Firehose protocol instance.
    /// <para>获取 Firehose 协议实例。</para>
    /// </summary>
    /// <value>The instance created with the session; always non-<c>null</c>.
    /// <para>随会话一同创建的实例；始终非 <c>null</c>。</para></value>
    public FirehoseProtocol? Firehose { get; }

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
        FirehoseProtocol.ValidateAuthenticationTimeout(options.AuthenticationTimeoutMs);
        options.ProjectAuthentication?.Validate();
        options.Vip?.Validate();
        if (options.ProjectAuthentication is not null && options.XiaomiAuthenticationProvider is not null)
        {
            throw new ArgumentException("Select one vendor authentication family.", nameof(options));
        }
        if (options.SendOnePlusNetType && (options.XiaomiAuthenticationProvider is not null
            || options.ProjectAuthentication?.Kind is EdlProjectAuthenticationKind.NothingProject or EdlProjectAuthenticationKind.OnePlusProject))
        {
            throw new ArgumentException("The selected SetNetType startup path cannot be combined with this authentication variant.", nameof(options));
        }
        ThrowIfDisposed();
        using var operation = await Firehose!.EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        await InitializeOwnedAsync(programmerImage, cancellationToken, options).ConfigureAwait(false);
    }

    /// <summary>Initializes while the caller holds this session's Firehose operation lease.</summary>
    internal async Task InitializeOwnedAsync(Stream? programmerImage, CancellationToken cancellationToken,
        EdlInitializationOptions? options = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConfigured)
        {
            if (options?.XiaomiAuthenticationProvider is not null || options?.ProjectAuthentication is not null
                || options?.Vip is not null || options?.SendOnePlusNetType == true)
            {
                throw new InvalidOperationException("Vendor startup requirements must be selected before session initialization.");
            }
            if (options?.QueryIdentityBeforeUpload == true && Sahara?.DeviceIdentity is null)
            {
                throw new InvalidOperationException("The configured session has no verified Sahara identity report.");
            }
            return;
        }

        try
        {
            if (options?.Vip is { } vip)
            {
                if (_protocolState == EdlProtocolState.Firehose)
                {
                    throw new InvalidOperationException("VIP must be selected before Firehose probing changes the packet sequence.");
                }
                Firehose!.ConfigureVipOwned(vip);
            }
            if (_protocolState != EdlProtocolState.Firehose)
            {
                Sahara = new SaharaProtocol(Transport)
                {
                    QudHelloRecoveryEnabled = options?.QudHelloRecoveryEnabled ?? false,
                    AllowSingleFirehosePendingCompletion = options?.AllowSingleFirehosePendingCompletion ?? false,
                    QueryIdentityBeforeUpload = options?.QueryIdentityBeforeUpload ?? false
                };
                await Sahara.HandshakeAsync(programmerImage, cancellationToken: cancellationToken).ConfigureAwait(false);
                SetProtocolState(EdlProtocolState.Firehose);
            }
            else if (options?.QueryIdentityBeforeUpload == true)
            {
                throw new SaharaException("A Firehose-only connection cannot provide the required Sahara identity.");
            }
            if (options?.XiaomiAuthenticationProvider is { } provider)
            {
                await Firehose!.AuthenticateXiaomiOwnedAsync(provider, Sahara?.DeviceIdentity,
                    options.AuthenticationTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            var project = options?.ProjectAuthentication;
            if (project is not null && Sahara?.DeviceIdentity is { } identity && identity.SerialNumber != project.SerialNumber)
            {
                throw new FirehoseException("The selected project authentication serial does not match Sahara.");
            }
            var netType = options?.SendOnePlusNetType == true;
            var demacia = project?.Kind == EdlProjectAuthenticationKind.OnePlusDemacia;
            await Firehose!.ConfigureCoreAsync(30, cancellationToken, deferStorageDetection: project is not null || netType).ConfigureAwait(false);
            if (demacia)
            {
                await Firehose.AuthenticateProjectOwnedAsync(project!, options!.AuthenticationTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            if (netType)
            {
                await Firehose.SetOnePlusNetTypeOwnedAsync(options!.AuthenticationTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            if (project is not null && !demacia)
            {
                await Firehose.AuthenticateProjectOwnedAsync(project, options!.AuthenticationTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            if (project is not null || netType)
            {
                await Firehose.CompleteStorageConfigurationOwnedAsync(cancellationToken).ConfigureAwait(false);
            }
            IsConfigured = true;
        }
        catch
        {
            SetProtocolState(EdlProtocolState.Unknown);
            Firehose!.Invalidate();
            throw;
        }
    }

    /// <summary>
    /// Ensures the session is initialized (handshake + configure).
    /// <para>确保会话已初始化（握手 + 配置）。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream. <para>可选的编程器镜像流。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    public async Task EnsureInitializedAsync(Stream? programmerImage = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured)
        {
            await InitializeAsync(programmerImage, cancellationToken);
        }
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
    /// Throws <see cref="ObjectDisposedException"/> if the session has been disposed.
    /// <para>如果会话已释放，则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EdlSession));
        }

        if (Firehose?.RequiresReconnect == true)
        {
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
        Firehose?.Dispose();
        Transport.Dispose();
    }
}