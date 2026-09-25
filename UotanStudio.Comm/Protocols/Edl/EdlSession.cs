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

    /// <summary>
    /// Creates a new EDL session over the specified device transport.
    /// <para>在指定设备传输上创建新的 EDL 会话。</para>
    /// </summary>
    /// <param name="transport">The device transport. 设备传输。</param>
    /// <param name="firehoseConfig">Optional Firehose configuration. 可选的 Firehose 配置。</param>
    internal EdlSession(ICommTransport transport, FirehoseConfiguration? firehoseConfig = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Firehose = new FirehoseProtocol(transport, firehoseConfig);
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

    /// <summary>Initializes while the caller holds this session's Firehose operation lease.</summary>
    internal async Task InitializeOwnedAsync(Stream? programmerImage, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConfigured)
        {
            return;
        }

        try
        {
            Sahara = new SaharaProtocol(Transport);
            await Sahara.HandshakeAsync(programmerImage, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Firehose!.ConfigureCoreAsync(30, cancellationToken).ConfigureAwait(false);
            IsConfigured = true;
        }
        catch
        {
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