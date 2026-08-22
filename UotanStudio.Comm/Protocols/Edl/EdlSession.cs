using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Represents an EDL (Emergency Download Mode) protocol session combining Sahara and Firehose protocols.
/// <para>表示 EDL（紧急下载模式）协议会话，组合 Sahara 和 Firehose 协议。</para>
/// </summary>
public sealed class EdlSession : IProtocolSession
{
    private readonly IUsbDeviceSession _transport;
    private SaharaProtocol? _sahara;
    private FirehoseProtocol? _firehose;
    private bool _disposed;
    private bool _configured;

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    public string ProtocolName => "edl";

    /// <summary>
    /// Gets the underlying transport session.
    /// <para>获取底层传输会话。</para>
    /// </summary>
    public IUsbDeviceSession Transport => _transport;

    /// <summary>
    /// Gets whether the session is connected and ready.
    /// <para>获取会话是否已连接并就绪。</para>
    /// </summary>
    public bool IsConnected => !_disposed;

    /// <summary>
    /// Gets whether Firehose has been configured.
    /// <para>Firehose 是否已配置。</para>
    /// </summary>
    public bool IsConfigured => _configured;

    /// <summary>
    /// Gets the Sahara protocol instance.
    /// <para>获取 Sahara 协议实例。</para>
    /// </summary>
    public SaharaProtocol? Sahara => _sahara;

    /// <summary>
    /// Gets the Firehose protocol instance.
    /// <para>获取 Firehose 协议实例。</para>
    /// </summary>
    public FirehoseProtocol? Firehose => _firehose;

    /// <summary>
    /// Gets the negotiated maximum packet length from Sahara.
    /// <para>从 Sahara 获取协商的最大包长度。</para>
    /// </summary>
    public uint MaxPacketLength => _sahara?.MaxPacketLength ?? 4096;

    /// <summary>
    /// Creates a new EDL session over the specified USB transport.
    /// <para>在指定 USB 传输上创建新的 EDL 会话。</para>
    /// </summary>
    /// <param name="transport">The USB transport session. USB 传输会话。</param>
    /// <param name="firehoseConfig">Optional Firehose configuration. 可选的 Firehose 配置。</param>
    public EdlSession(IUsbDeviceSession transport, FirehoseConfiguration? firehoseConfig = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _firehose = new FirehoseProtocol(transport, firehoseConfig);
    }

    /// <summary>
    /// Performs Sahara handshake (if needed) and Firehose configure.
    /// <para>执行 Sahara 握手（如需要）和 Firehose 配置。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream. 可选的编程器镜像流。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    public async Task InitializeAsync(Stream? programmerImage = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Try Sahara handshake
        _sahara = new SaharaProtocol(_transport);
        SaharaMode mode = await _sahara.HandshakeAsync(programmerImage, cancellationToken: cancellationToken);

        if (mode == SaharaMode.ImageTxComplete)
        {
            // Device was already in Firehose mode
            _configured = true;
            return;
        }

        // Configure Firehose
        await _firehose!.ConfigureAsync(cancellationToken: cancellationToken);
        _configured = true;
    }

    /// <summary>
    /// Ensures the session is initialized (handshake + configure).
    /// <para>确保会话已初始化（握手 + 配置）。</para>
    /// </summary>
    /// <param name="programmerImage">Optional programmer image stream. 可选的编程器镜像流。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>A task representing the asynchronous operation. 表示异步操作的任务。</returns>
    public async Task EnsureInitializedAsync(Stream? programmerImage = null, CancellationToken cancellationToken = default)
    {
        if (!_configured)
            await InitializeAsync(programmerImage, cancellationToken);
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
    public async Task<FirehoseResponse> FlashPartitionAsync(Stream dataStream, long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await _firehose!.ProgramAsync(dataStream, startSector, numSectors, filename, physicalPartition, cancellationToken);
    }

    /// <summary>
    /// Erases a partition.
    /// <para>擦除分区。</para>
    /// </summary>
    /// <param name="startSector">The starting sector. 起始扇区。</param>
    /// <param name="numSectors">The number of sectors. 扇区数量。</param>
    /// <param name="physicalPartition">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> ErasePartitionAsync(long startSector, long numSectors, int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await _firehose!.EraseAsync(startSector, numSectors, physicalPartition, cancellationToken);
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
    public async Task<(FirehoseResponse Response, byte[] Data)> ReadPartitionAsync(long startSector, long numSectors, string filename = "partition.bin", int physicalPartition = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await _firehose!.ReadAsync(startSector, numSectors, filename, physicalPartition, cancellationToken);
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
        await EnsureInitializedAsync(cancellationToken: cancellationToken);
        return await _firehose!.ResetAsync(cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if the session has been disposed.
    /// <para>如果会话已释放，则抛出 <see cref="ObjectDisposedException"/>。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EdlSession));
    }

    /// <summary>
    /// Disposes the session and releases all resources.
    /// <para>释放会话并释放所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sahara?.Dispose();
        _firehose?.Dispose();
        _transport.Dispose();
    }
}
