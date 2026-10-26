using System.Buffers;
using System.Buffers.Binary;
using System.IO.Ports;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Protocols.Mtk.Upload;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Managed MTK boot protocol over an already selected transport. Initialization only queries
/// identity; DA upload and execution require separate explicit calls.
/// <para>在已选定的传输之上管理的 MTK 启动协议。初始化仅查询身份信息；DA 上传与执行需要单独显式调用。</para>
/// </summary>
public sealed partial class MtkSession : IProtocolSession
{
    private const int MaximumAgentLength = 64 * 1024 * 1024;
    private const int AgentChunkLength = 1024;
    internal const int MaximumBootReadLength = 1024 * 1024;
    private const int MaximumXmlSourcePackets = 65536;
    private const int BootHandshakeAttempts = 5;
    private const int HandshakeDrainTimeoutMs = 50;
    private const int HandshakeDrainLength = 0x100;
    private static readonly byte[] BootHandshakeCommand = [0xA0, 0x0A, 0x50, 0x05];
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly List<ProtocolEvidence> _evidence = [];
    private readonly int _timeoutMs;
    private readonly int _handshakeTimeoutMs;
    private MtkSessionState _state;
    private MtkDaUploadStage _uploadStage;
    private bool _uploadCompletionPending;
    private uint _agentAddress;
    private PhysicalDeviceIdentity? _physicalIdentity;
    private ushort? _usbVendorId;
    private ushort? _usbProductId;
    private volatile bool _disposed;

    /// <summary>Creates a session which takes ownership of the selected transport.</summary>
    /// <param name="transport">An open transport for one explicitly selected device.</param>
    /// <param name="timeoutMs">The time budget for each command field or data transfer.</param>
    /// <param name="handshakeTimeoutMs">The short per-byte handshake budget for transient preloader ports.</param>
    internal MtkSession(ICommTransport transport, int timeoutMs = 5000, int handshakeTimeoutMs = 100)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(handshakeTimeoutMs);
        Transport = transport;
        _timeoutMs = timeoutMs;
        _handshakeTimeoutMs = handshakeTimeoutMs;
    }

    /// <summary>
    /// The protocol identifier reported for this session.
    /// <para>本会话上报的协议标识。</para>
    /// </summary>
    /// <value>
    /// The constant string <c>mtk</c>.
    /// <para>常量字符串 <c>mtk</c>。</para>
    /// </value>
    public string ProtocolName => "mtk";

    /// <summary>
    /// The transport this session took ownership of at construction.
    /// <para>本会话在构造时接管的传输。</para>
    /// </summary>
    /// <value>
    /// The open transport used for every boot-protocol read and write; it is disposed together with the session.
    /// <para>用于所有 boot 协议读写的已打开传输；会随会话一同释放。</para>
    /// </value>
    public ICommTransport Transport { get; }

    /// <summary>
    /// Whether the transport is connected while the session remains in a usable stage.
    /// <para>在会话处于可用阶段时，传输是否仍处于连接状态。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the session is not disposed, the transport reports a connection, and the stage is neither faulted nor terminal.
    /// <para>当会话未释放、传输报告已连接，且阶段既非故障也非终态时返回 <see langword="true"/>。</para>
    /// </value>
    public bool IsConnected => !_disposed && Transport.IsConnected
        && _state is not (MtkSessionState.Faulted or MtkSessionState.XFlashShutdownAcknowledged
            or MtkSessionState.XmlRebootAcknowledged or MtkSessionState.LegacyShutdownAcknowledged);

    /// <summary>
    /// Gets the current protocol stage; AgentStarted does not imply storage readiness.
    /// <para>获取当前协议阶段；AgentStarted 并不意味着存储已就绪。</para>
    /// </summary>
    /// <value>
    /// The stage reached by completed commands, or <c>Disposed</c> once the session is released.
    /// <para>由已完成命令推进到的阶段；会话释放后为 <c>Disposed</c>。</para>
    /// </value>
    public MtkSessionState State => _disposed ? MtkSessionState.Disposed : _state;

    /// <summary>
    /// Gets the queried device identity, or null before successful initialization.
    /// <para>获取已查询到的设备身份；初始化成功之前为 null。</para>
    /// </summary>
    /// <value>
    /// The identity read during the boot handshake, or <see langword="null"/> until <c>InitializeAsync</c> completes.
    /// <para>在 boot 握手中读取到的身份信息；在 <c>InitializeAsync</c> 完成前为 <see langword="null"/>。</para>
    /// </value>
    public MtkDeviceInfo? DeviceInfo { get; private set; }

    /// <summary>
    /// Gets the immutable observations this session recorded during discovery, profile gating, and
    /// upload failures; serialised consumers must read these rather than message text.
    /// <para>获取本会话在发现、配置门控与上传失败期间记录的不可变观测；序列化消费者必须读取这些记录而非消息文本。</para>
    /// </summary>
    /// <value>
    /// The evidence recorded so far, in observation order.
    /// <para>按观测顺序记录的证据。</para>
    /// </value>
    public IReadOnlyList<ProtocolEvidence> Evidence => _evidence;

    /// <summary>
    /// Gets the descriptor-level USB stage classified when this session was opened or adopted, or
    /// <see langword="null"/> when no classification ran.
    /// <para>获取本会话打开或被接管时分类出的描述符级 USB 阶段；未分类时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>
    /// The classification with its reason, or <see langword="null"/>.
    /// <para>带原因的分类结果，或 <see langword="null"/>。</para>
    /// </value>
    public MtkModeClassification? DiscoveredUsbMode { get; private set; }

    /// <summary>
    /// Gets the furthest download-agent bring-up stage this session has entered.
    /// <para>获取本会话已进入的最远下载代理启动阶段。</para>
    /// </summary>
    /// <value>
    /// The current stage; <see cref="MtkDaUploadStage.NotStarted"/> before any boot traffic.
    /// <para>当前阶段；在任何 boot 通信之前为 <see cref="MtkDaUploadStage.NotStarted"/>。</para>
    /// </value>
    public MtkDaUploadStage UploadStage => _uploadStage;

    /// <summary>
    /// Gets the frozen result of the most recent failed bring-up attempt, or <see langword="null"/>
    /// when no attempt has failed. This is read after the session faults to decide whether a replay
    /// is admissible or only a reconnect.
    /// <para>获取最近一次失败的启动尝试的定格结果；没有失败时为 <see langword="null"/>。
    /// 会话进入 fault 后读取它，以判定允许重放还是只能重新连接。</para>
    /// </summary>
    /// <value>
    /// The last failure result, or <see langword="null"/>.
    /// <para>最近一次失败的结果，或 <see langword="null"/>。</para>
    /// </value>
    public MtkDaUploadResult? LastUploadResult { get; private set; }

    /// <summary>
    /// Opens a named COM/TTY port without RTS/CTS flow control and queries its bootloader.
    /// <para>以无 RTS/CTS 流控的方式打开指定的 COM/TTY 端口，并查询其 bootloader。</para>
    /// </summary>
    /// <param name="portName">
    /// The explicitly selected port; no fallback or enumeration-order matching is performed.
    /// <para>显式选定的端口；不进行回退或按枚举顺序匹配。</para>
    /// </param>
    /// <param name="baudRate">
    /// The serial line speed.
    /// <para>串口波特率。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The operation cancellation token.
    /// <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// An initialized bootloader session which owns the serial transport.
    /// <para>已初始化的 bootloader 会话，拥有该串口传输。</para>
    /// </returns>
    public static async Task<MtkSession> OpenSerialAsync(string portName, int baudRate = 921600,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        var transport = new SerialTransport(portName, baudRate, handshake: Handshake.None);
        var session = new MtkSession(transport);
        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a session over an already-opened USB bulk session (BROM PID 0E8D:0003 or
    /// preloader/DA 0E8D:2000|2001). The BROM/XFlash command set is identical over USB
    /// (mtkclient uses the same byte protocol on the bulk endpoints); the command interface
    /// selection is now verified against the observed descriptors before the first protocol byte.
    /// <para>在已打开的 USB 批量端点会话上打开会话（BROM PID 0E8D:0003 或
    /// preloader/DA 0E8D:2000|2001）。BROM/XFlash 命令集在 USB 上与串口一致（mtkclient
    /// 在 bulk 端点使用同一字节协议）；命令接口选择现在会在第一个协议字节前依据已观测描述符进行校验。</para>
    /// </summary>
    /// <param name="session">An opened USB device session bound to the command interface.
    /// 已打开并绑定命令接口的 USB 设备会话。</param>
    /// <param name="cancellationToken">The operation cancellation token. 取消令牌。</param>
    /// <returns>An initialized bootloader session which owns the USB transport.
    /// 拥有 USB 传输的已初始化 bootloader 会话。</returns>
    internal static async Task<MtkSession> OpenUsbAsync(IUsbDeviceSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await OpenUsbCoreAsync(new UsbDeviceTransport(session), session.DeviceInfo, options: null,
            session.EndpointIn, session.EndpointOut, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a session over a transport already bound to the command endpoints of the described USB device, verifying the descriptor-level mode and command-interface selection before any protocol byte is exchanged. Ambiguous or unexpected interfaces are rejected without device I/O.
    /// <para>在已绑定到所描述 USB 设备命令端点的传输上开会话，在任何协议字节交互之前先校验描述符层面的模式与命令接口选择。接口歧义或不符合预期时直接拒绝，不进行设备 I/O。</para></summary>
    /// <param name="transport">An open transport bound to the caller-selected endpoints.
    /// 已绑定到调用方所选端点的打开传输。</param>
    /// <param name="device">The observed descriptors of the device behind the transport. 传输之后设备的已观测描述符。</param>
    /// <param name="options">Optional expectations; <see langword="null"/>keeps compatible behaviour.
    /// 可选的期望条件；为 <see langword="null"/>时保持兼容行为。</param>
    /// <param name="cancellationToken">The operation cancellation token. 取消令牌。</param>
    /// <returns>An initialized bootloader session which owns the transport. 拥有该传输的已初始化 bootloader 会话。</returns>
    /// <exception cref="InvalidOperationException">The endpoint presents a logging-only or otherwise unusable command interface, or the classified mode contradicts <see cref="MtkOpenOptions.ExpectedMode"/>; no protocol byte was exchanged.
    /// <para>端点仅呈现日志接口或其他不可用命令接口，或分类模式与 <see cref="MtkOpenOptions.ExpectedMode"/> 矛盾；未交换任何协议字节。</para></exception>
    public static Task<MtkSession> OpenUsbAsync(ICommTransport transport, UsbDeviceInfo device,
        MtkOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(device);
        return OpenUsbCoreAsync(transport, device, options, boundEndpointIn: 0, boundEndpointOut: 0, cancellationToken);
    }

    private static async Task<MtkSession> OpenUsbCoreAsync(ICommTransport transport, UsbDeviceInfo device,
        MtkOpenOptions? options, byte boundEndpointIn, byte boundEndpointOut, CancellationToken cancellationToken)
    {
        var session = new MtkSession(transport);
        try
        {
            var classification = MtkModeClassifier.Classify(device);
            var selection = MtkCommandInterfacePolicy.Select(device, options, boundEndpointIn, boundEndpointOut);
            session.NoteUsbDescriptor(device);
            session.DiscoveredUsbMode = classification;
            session._physicalIdentity = PhysicalDeviceIdentity.From(device);
            session._evidence.Add(new ProtocolEvidence
            {
                Protocol = "mtk",
                Phase = "mode-discovery",
                Outcome = classification.Mode == MtkUsbMode.Unknown
                    ? CommandOutcomeKind.Unknown : CommandOutcomeKind.Completed,
                Detail = classification.Reason
            });
            session.NoteCommandInterfaceSelection(selection);
            if (classification.Mode == MtkUsbMode.LoggingOnly)
            {
                throw new InvalidOperationException($"The endpoint is logging-only and must not receive commands: {classification.Reason}");
            }

            if (options is { ExpectedMode: not MtkUsbMode.Unknown } expectation
                && classification.Mode != expectation.ExpectedMode)
            {
                throw new InvalidOperationException(
                    $"The endpoint was expected to present {expectation.ExpectedMode} but classified as {classification.Mode}: {classification.Reason}");
            }

            if (selection.Status != MtkCommandInterfaceStatus.Selected && selection.Status != MtkCommandInterfaceStatus.NoMetadata)
            {
                throw new InvalidOperationException($"No unambiguous command interface could be selected: {selection.Reason}");
            }

            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Performs the complemented-byte handshake and reads hardware identity and security flags.
    /// <para>执行反码字节握手，并读取硬件身份信息与安全标志。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// The operation cancellation token.
    /// <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// The queried identity. No watchdog, memory write, authentication bypass, or reset is performed.
    /// <para>查询到的设备身份信息。不执行看门狗、内存写入、认证绕过或复位操作。</para>
    /// </returns>
    public async Task<MtkDeviceInfo> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Created);
            cancellationToken.ThrowIfCancellationRequested();
            NoteUploadStage(MtkDaUploadStage.Handshake);
            try
            {
                await RunBootHandshakeOwnedAsync(cancellationToken).ConfigureAwait(false);

                NoteUploadStage(MtkDaUploadStage.ChipInfoRequested);
                await EchoAsync([0xFD], cancellationToken).ConfigureAwait(false);
                var hardware = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
                await EchoAsync([0xFC], cancellationToken).ConfigureAwait(false);
                var versions = await ReadAsync(8, _timeoutMs, cancellationToken).ConfigureAwait(false);
                await EchoAsync([0xD8], cancellationToken).ConfigureAwait(false);
                var configuration = await ReadAsync(6, _timeoutMs, cancellationToken).ConfigureAwait(false);
                CheckStatus(BinaryPrimitives.ReadUInt16BigEndian(configuration.AsSpan(4)), "target configuration query");
                var identity = await QueryBootIdentityOwnedAsync(cancellationToken).ConfigureAwait(false);

                ThrowIfDisposed();
                DeviceInfo = new MtkDeviceInfo(
                    BinaryPrimitives.ReadUInt16BigEndian(hardware),
                    BinaryPrimitives.ReadUInt16BigEndian(versions),
                    BinaryPrimitives.ReadUInt16BigEndian(versions.AsSpan(2)),
                    BinaryPrimitives.ReadUInt16BigEndian(versions.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32BigEndian(configuration),
                    identity.BootLoaderVersion,
                    identity.BromVersion,
                    identity.Meid,
                    identity.Socid);
                _state = MtkSessionState.Bootloader;
                return DeviceInfo;
            }
            catch (Exception error)
            {
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Runs the complemented-byte boot handshake, using the reference's relaxed resynchronization only when descriptor evidence proves a non-Boot-ROM USB product id.
    /// <para>执行反码字节 boot 握手；仅当描述符证据证明是非 Boot ROM 的 USB 产品 ID 时，才使用参考实现的宽松重同步。</para></summary>
    /// <remarks>The Boot ROM product id keeps the strict single pass. Every other known USB product id first writes
    /// 0xA0 without reading an echo, then retries the full four-byte sequence up to five times, draining stale input
    /// and restarting from the first byte after each mismatch, as <c>mtkclient/Library/Port.py:147-192</c> does.
    /// <para>Boot ROM 产品 ID 保持严格单趟语义。其他已知 USB 产品 ID 先单独写入 0xA0 且不读取回显，随后最多重试完整
    /// 四字节序列五次；每次不匹配后清空陈旧输入并从首字节重同步，与 <c>mtkclient/Library/Port.py:147-192</c> 一致。</para></remarks>
    /// <param name="cancellationToken">The operation cancellation token. 操作取消令牌。</param>
    private async Task RunBootHandshakeOwnedAsync(CancellationToken cancellationToken)
    {
        if (!UsesRelaxedBootHandshake)
        {
            foreach (var value in BootHandshakeCommand)
            {
                await WriteAsync([value], _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
                var reply = await ReadAsync(1, _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (reply[0] != (byte)(value ^ 0xFF))
                {
                    throw new MtkProtocolException("The boot handshake echo did not match.");
                }
            }

            return;
        }

        await WriteAsync([BootHandshakeCommand[0]], _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < BootHandshakeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryRunRelaxedBootHandshakeAttemptOwnedAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await DiscardHandshakeInputOwnedAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new MtkProtocolException(
            $"The boot handshake did not synchronize after {BootHandshakeAttempts} attempts.");
    }

    /// <summary>Gets whether the observed USB product id is not a documented Boot ROM port.
    /// <para>获取已观测 USB 产品 ID 是否不是已记载的 Boot ROM 端口。</para></summary>
    /// <value><see langword="true"/> only when a USB descriptor was recorded and the pair is not a Boot ROM port;
    /// serial transports and unknown ports keep the strict path.
    /// <para>仅在已记录 USB 描述符且该组合不是 Boot ROM 端口时为 <see langword="true"/>；串口传输与未知端口保持严格路径。</para></value>
    private bool UsesRelaxedBootHandshake => _usbProductId is { } productId && !IsBootRomUsbProduct(productId);

    private bool IsBootRomUsbProduct(ushort productId) =>
        productId == 0x0003
        || (_usbVendorId == 0x0FCE
            && productId is 0xF200 or 0xD1E9 or 0xD1E2 or 0xD1EC or 0xD1DD);

    private async Task<bool> TryRunRelaxedBootHandshakeAttemptOwnedAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var value in BootHandshakeCommand)
            {
                await WriteAsync([value], _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
                var reply = await ReadAsync(1, _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (reply[0] != (byte)(value ^ 0xFF))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception error) when (error is TimeoutException or MtkProtocolException)
        {
            return false;
        }
    }

    private async Task DiscardHandshakeInputOwnedAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[HandshakeDrainLength];
        try
        {
            _ = await Transport.ReadAsync(buffer, 0, buffer.Length, HandshakeDrainTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>Uploads one explicitly selected, word-aligned DA image including its signature bytes. Authentication failures are reported; no image patching or fallback agent is attempted.
    /// <para>上传一个显式选定、按字对齐的 DA 镜像（含其签名字节）。认证失败会被上报； 不进行镜像修补，也不尝试任何回退代理。</para></summary>
    /// <param name="image">A readable stream positioned at the agent image.
    /// <para>定位到代理镜像起始处、可读的流。</para></param>
    /// <param name="loadAddress">The validated 32-bit target load address supplied by the device profile.
    /// <para>由设备配置提供、已校验的 32 位目标加载地址。</para></param>
    /// <param name="length">The total image length, including the signature, limited to 64 MiB.
    /// <para>镜像总长度（含签名），上限为 64 MiB。</para></param>
    /// <param name="signatureLength">The signature suffix length included in the image.
    /// <para>镜像中包含的签名后缀长度。</para></param>
    /// <param name="progress">Optional cumulative uploaded-byte progress.
    /// <para>可选的累计已上传字节进度。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A task that completes once the device confirms the uploaded agent checksum and the session advances to the agent-uploaded stage.
    /// <para>在设备确认已上传代理的校验和、会话推进到代理已上传阶段后完成的任务。</para></returns>
    public Task UploadDownloadAgentAsync(Stream image, uint loadAddress, int length, int signatureLength = 0,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => UploadDownloadAgentCoreAsync(image, loadAddress, length, signatureLength, progress, cancellationToken);

    private async Task UploadDownloadAgentCoreAsync(Stream image, uint loadAddress, int length, int signatureLength,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumAgentLength);
        ArgumentOutOfRangeException.ThrowIfNegative(signatureLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(signatureLength, length);
        if ((length & 1) != 0 || (loadAddress & 3) != 0 || (ulong)loadAddress + (uint)length > (ulong)uint.MaxValue + 1)
        {
            throw new ArgumentException("The agent image must be word-aligned and fit the 32-bit load address range.");
        }

        if (!image.CanRead || (image.CanSeek && image.Length - image.Position < length))
        {
            throw new ArgumentException("The agent source is unreadable or shorter than its declared length.", nameof(image));
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Bootloader);
            cancellationToken.ThrowIfCancellationRequested();
            NoteUploadStage(MtkDaUploadStage.AgentRequest);
            try
            {
                await EchoAsync([0xD7], cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async(loadAddress, cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async((uint)length, cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async((uint)signatureLength, cancellationToken).ConfigureAwait(false);
                var requestStatus = BinaryPrimitives.ReadUInt16BigEndian(
                    await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
                CheckStatus(requestStatus, "DA upload request");

                NoteUploadStage(MtkDaUploadStage.DataTransferring);
                var buffer = ArrayPool<byte>.Shared.Rent(AgentChunkLength);
                ushort checksum = 0;
                try
                {
                    var uploaded = 0;
                    while (uploaded < length)
                    {
                        var count = Math.Min(AgentChunkLength, length - uploaded);
                        await image.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        for (var offset = 0; offset < count; offset += 2)
                        {
                            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
                        }

                        await WriteAsync(buffer, count, _timeoutMs, cancellationToken).ConfigureAwait(false);
                        uploaded += count;
                        progress?.Report(uploaded);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                _uploadCompletionPending = true;
                var completion = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
                _uploadCompletionPending = false;
                CheckStatus(BinaryPrimitives.ReadUInt16BigEndian(completion.AsSpan(2)), "DA upload completion");
                var reportedChecksum = BinaryPrimitives.ReadUInt16BigEndian(completion);
                if (reportedChecksum == 0)
                {
                    RecordSecurityEvidence("da-upload-checksum", CommandOutcomeKind.Completed,
                        "warning: the device reported a zero agent checksum; continuing as the reference does.");
                }
                else if (reportedChecksum != checksum)
                {
                    RecordSecurityEvidence("da-upload-checksum", CommandOutcomeKind.Completed,
                        $"warning: the device reported agent checksum 0x{reportedChecksum:X4} instead of the computed " +
                        $"0x{checksum:X4}; continuing as the reference does.");
                }

                NoteUploadStage(MtkDaUploadStage.ChecksumConfirmed);
                ThrowIfDisposed();
                _agentAddress = loadAddress;
                _state = MtkSessionState.AgentUploaded;
            }
            catch (Exception error)
            {
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Requests execution of the successfully uploaded agent at its recorded address.
    /// <para>请求在已记录地址处执行先前成功上传的代理。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// The operation cancellation token.
    /// <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the target acknowledges execution and the session advances to the agent-started stage.
    /// <para>在目标确认执行、会话推进到代理已启动阶段后完成的任务。</para>
    /// </returns>
    public async Task StartDownloadAgentAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.AgentUploaded);
            cancellationToken.ThrowIfCancellationRequested();
            NoteUploadStage(MtkDaUploadStage.AgentStarted);
            try
            {
                await EchoAsync([0xD5], cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async(_agentAddress, cancellationToken).ConfigureAwait(false);
                var status = await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt16BigEndian(status) != 0)
                {
                    throw new MtkProtocolException("The target rejected execution of the uploaded agent.",
                        BinaryPrimitives.ReadUInt16BigEndian(status));
                }

                ThrowIfDisposed();
                _state = MtkSessionState.AgentStarted;
            }
            catch (Exception error)
            {
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task EchoUInt32Async(uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        await EchoAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task EchoAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await WriteAsync(bytes, _timeoutMs, cancellationToken).ConfigureAwait(false);
        var echo = await ReadAsync(bytes.Length, _timeoutMs, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(echo))
        {
            throw new MtkProtocolException("The boot command or parameter echo did not match.");
        }
    }

    private async Task ReadStatusAsync(string operation, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false);
        CheckStatus(BinaryPrimitives.ReadUInt16BigEndian(bytes), operation);
    }

    private static void CheckStatus(ushort status, string operation)
    {
        if (status > 0xFF)
        {
            throw new MtkProtocolException($"The target rejected {operation} (status 0x{status:X4}).", status);
        }
    }

    private Task WriteAsync(byte[] bytes, int timeoutMs, CancellationToken cancellationToken)
        => WriteAsync(bytes, bytes.Length, timeoutMs, cancellationToken);

    /// <summary>
    /// The single-byte <c>0x5A</c> acknowledgement the Legacy protocol expects between packets; a shared constant
    /// so per-packet loops stop allocating a fresh one-byte array for every exchange.
    /// <para>Legacy 协议在包间期望的单字节 <c>0x5A</c> 确认；共享常量使逐包循环不再为每次交换分配新的一字节数组。</para>
    /// </summary>
    private static readonly byte[] Acknowledge = [0x5A];

    /// <summary>Writes one buffer slice with the shared incomplete-write guard.
    /// <para>使用共用的不完整写入守卫写出一个缓冲区切片。</para></summary>
    /// <param name="bytes">The source buffer. 源缓冲区。</param>
    /// <param name="offset">The slice start. 切片起始。</param>
    /// <param name="count">The slice length. 切片长度。</param>
    /// <param name="timeoutMs">The time budget. 时间预算。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private async Task WriteAsync(byte[] bytes, int offset, int count, int timeoutMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var written = await Transport.WriteAsync(bytes, offset, count, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (written != count)
        {
            throw new MtkProtocolException("Incomplete boot protocol write; the session cannot be resumed.");
        }
    }

    private async Task WriteAsync(byte[] bytes, int count, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var written = await Transport.WriteAsync(bytes, 0, count, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (written != count)
        {
            throw new MtkProtocolException("Incomplete boot protocol write; the session cannot be resumed.");
        }
    }

    /// <summary>
    /// Bounds one boot-protocol receive before any buffer is allocated. Every length reaching the
    /// boot readers originates from device-reported counts, so the ceiling is checked first and
    /// matches the largest bounded agent or storage packet the protocol families negotiate.
    /// <para>在分配任何缓冲区之前约束单次 boot 协议接收。到达 boot 读取器的长度都来自设备上报的计数，
    /// 因此先校验上限，其值等于各协议族协商的最大有界代理或存储包。
    /// </para>
    /// </summary>
    /// <param name="count">The requested byte count. 请求的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or above
    /// <see cref="MaximumBootReadLength"/>. <para><paramref name="count"/> 为负或超过
    /// <see cref="MaximumBootReadLength"/>。</para></exception>
    internal static void ValidateBootReadLength(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumBootReadLength);
    }

    private async Task<byte[]> ReadAsync(int count, int timeoutMs, CancellationToken cancellationToken)
    {
        ValidateBootReadLength(count);
        var bytes = new byte[count];
        await ReadIntoAsync(bytes, count, timeoutMs, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into the caller's buffer under the same deadline, truncation and
    /// disposal guards as the allocating reader, so per-packet loops reuse one buffer instead of allocating per
    /// exchange.
    /// <para>在与分配式读取器相同的期限、截断与释放守卫下，向调用方缓冲精确读取 <paramref name="count"/> 字节，
    /// 使逐包循环复用同一缓冲而不必每次交换都分配。</para>
    /// </summary>
    /// <param name="buffer">Buffer receiving the bytes.<para>接收字节的缓冲。</para></param>
    /// <param name="count">Byte count to read; at most the buffer length.<para>读取的字节数；不超过缓冲长度。</para></param>
    /// <param name="timeoutMs">The time budget.<para>时间预算。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    private async Task ReadIntoAsync(byte[] buffer, int count, int timeoutMs, CancellationToken cancellationToken)
    {
        ValidateBootReadLength(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
        var position = 0;
        var started = Environment.TickCount64;
        while (position < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var remainingMs = timeoutMs - (Environment.TickCount64 - started);
            if (remainingMs <= 0)
            {
                throw new TimeoutException("The boot protocol receive deadline expired.");
            }

            var received = await Transport.ReadAsync(buffer, position, count - position,
                (int)remainingMs, cancellationToken).ConfigureAwait(false);
            if (received <= 0 || received > count - position)
            {
                throw new MtkProtocolException("The boot protocol response was truncated or timed out.");
            }

            position += received;
        }
    }

    private void RequireState(MtkSessionState expected)
    {
        ThrowIfDisposed();
        if (!Transport.IsConnected || _state != expected)
        {
            throw new InvalidOperationException($"MTK operation requires an open transport in state {expected}; current state is {_state}.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Advances the recorded bring-up stage; stages are monotonic so a later protocol family
    /// cannot rewind the frozen failure context of an earlier attempt.
    /// <para>推进记录的启动阶段；阶段单调递增，因此后续协议族不会回退早期尝试的定格失败上下文。
    /// </para>
    /// </summary>
    /// <param name="stage">The stage being entered. 正在进入的阶段。</param>
    internal void NoteUploadStage(MtkDaUploadStage stage)
    {
        if (stage > _uploadStage)
        {
            _uploadStage = stage;
        }
    }

    /// <summary>
    /// Freezes the current stage and the exception-classified outcome into
    /// <see cref="LastUploadResult"/> before the caller transitions to the faulted state.
    /// <para>在调用方转入故障状态之前，把当前阶段与按异常分类的结果定格进 <see cref="LastUploadResult"/>。
    /// </para>
    /// </summary>
    /// <param name="error">The observed failure. 观察到的失败。</param>
    internal void CaptureUploadFailure(Exception error)
    {
        var pending = _uploadCompletionPending;
        var slaEvidence = Interlocked.Exchange(ref _pendingSlaEvidence, null);
        var outcome = ClassifyUploadOutcome(error);
        if (pending && outcome is CommandOutcomeKind.Rejected or CommandOutcomeKind.Unknown)
        {
            outcome = CommandOutcomeKind.Unknown;
        }

        LastUploadResult = new MtkDaUploadResult
        {
            Reached = _uploadStage,
            RetrySafe = !pending && IsUploadReplaySafe(_uploadStage, outcome),
            Evidence = slaEvidence is null
                ?
                [
                    new ProtocolEvidence
                    {
                        Protocol = "mtk",
                        Phase = "da-upload",
                        Outcome = outcome,
                        Detail = error.Message
                    }
                ]
                :
                [
                    slaEvidence,
                    new ProtocolEvidence
                    {
                        Protocol = "mtk",
                        Phase = "da-upload",
                        Outcome = outcome,
                        Detail = error.Message
                    }
                ]
        };
        _pendingSlaEvidence = null;
    }

    /// <summary>
    /// Records a descriptor-level mode classification performed by an adoption path such as the
    /// device session factory; an <see cref="MtkUsbMode.Unknown"/> result is kept as evidence and
    /// never upgraded or downgraded by enumeration position.
    /// <para>记录由接管路径（如设备会话工厂）执行的描述符级模式分类；<see cref="MtkUsbMode.Unknown"/>
    /// 结果仅作为证据保留，绝不按枚举位置升级或降级。
    /// </para>
    /// </summary>
    /// <param name="classification">The classification with its reason. 带原因的分类结果。</param>
    internal void NoteModeDiscovery(MtkModeClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        DiscoveredUsbMode = classification;
        _evidence.Add(new ProtocolEvidence
        {
            Protocol = "mtk",
            Phase = "mode-discovery",
            Outcome = classification.Mode == MtkUsbMode.Unknown
                ? CommandOutcomeKind.Unknown : CommandOutcomeKind.Completed,
            Detail = classification.Reason
        });
    }

    /// <summary>Records the observed USB descriptor identity so the boot handshake can follow the upstream
    /// Boot ROM versus preloader distinction without probing the device.
    /// <para>记录已观测的 USB 描述符身份，使 boot 握手无需探测设备即可遵循上游的 Boot ROM 与 preloader 区分。</para>
    /// </summary>
    /// <param name="device">The observed descriptors of the device behind the transport.
    /// <para>传输之后设备的已观测描述符。</para></param>
    internal void NoteUsbDescriptor(UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _usbVendorId = device.VendorId;
        _usbProductId = device.ProductId;
    }

    /// <summary>Records a command-interface selection performed by an adoption path such as the device session
    /// factory; only <see cref="MtkCommandInterfaceStatus.Selected"/> and <see cref="MtkCommandInterfaceStatus.NoMetadata"/>
    /// are accepted there, so a logging-only or mismatched binding is evidence of a refusal.
    /// <para>记录由设备会话工厂等接管路径执行的命令接口选择；该路径只接受
    /// <see cref="MtkCommandInterfaceStatus.Selected"/> 与 <see cref="MtkCommandInterfaceStatus.NoMetadata"/>，
    /// 因此仅日志或不匹配的绑定会被记录为拒绝证据。</para>
    /// </summary>
    /// <param name="selection">The selection with its reason. 带原因的选择结果。</param>
    internal void NoteCommandInterfaceSelection(MtkCommandInterfaceResult selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _evidence.Add(new ProtocolEvidence
        {
            Protocol = "mtk",
            Phase = "command-interface-selection",
            Outcome = selection.Status == MtkCommandInterfaceStatus.Selected
                ? CommandOutcomeKind.Completed
                : selection.Status == MtkCommandInterfaceStatus.NoMetadata
                    ? CommandOutcomeKind.Unknown : CommandOutcomeKind.Rejected,
            Detail = selection.Reason
        });
    }

    private static CommandOutcomeKind ClassifyUploadOutcome(Exception error) => error switch
    {
        MtkProtocolException => CommandOutcomeKind.Rejected,
        TimeoutException => CommandOutcomeKind.Timeout,
        OperationCanceledException => CommandOutcomeKind.Cancelled,
        ObjectDisposedException => CommandOutcomeKind.ConnectionLost,
        IOException => CommandOutcomeKind.ConnectionLost,
        _ => CommandOutcomeKind.Unknown
    };

    private static bool IsUploadReplaySafe(MtkDaUploadStage reached, CommandOutcomeKind outcome) => outcome switch
    {
        CommandOutcomeKind.Rejected => true,
        CommandOutcomeKind.Cancelled => reached < MtkDaUploadStage.AgentStarted,
        CommandOutcomeKind.Timeout => reached < MtkDaUploadStage.DataTransferring,
        CommandOutcomeKind.ConnectionLost => reached < MtkDaUploadStage.AgentStarted,
        CommandOutcomeKind.Completed => true,
        _ => false
    };

    /// <summary>
    /// Marks the session disposed and releases the owned transport; further operations fail.
    /// <para>将会话标记为已释放并释放所持有的传输；后续操作将失败。</para>
    /// </summary>
    /// <remarks>Idempotent: calling <c>Dispose</c> more than once has no additional effect.
    /// <para>幂等：多次调用 <c>Dispose</c> 不会产生额外效果。</para></remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Transport.Dispose();
    }
}