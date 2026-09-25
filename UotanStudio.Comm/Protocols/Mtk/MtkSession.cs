using System.Buffers.Binary;
using System.IO.Ports;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Managed MTK boot protocol over an already selected transport. Initialization only queries
/// identity; DA upload and execution require separate explicit calls.
/// <para>在已选定的传输之上管理的 MTK 启动协议。初始化仅查询身份信息；DA 上传与执行需要单独显式调用。</para>
/// </summary>
public sealed partial class MtkSession : IProtocolSession
{
    private const int MaximumAgentLength = 64 * 1024 * 1024;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly int _timeoutMs;
    private readonly int _handshakeTimeoutMs;
    private MtkSessionState _state;
    private uint _agentAddress;
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
    /// <see langword="true"/> when the session is not disposed, the transport reports a connection, and the stage is neither faulted nor XFlash-shutdown-acknowledged.
    /// <para>当会话未释放、传输报告已连接，且阶段既非故障也非已确认 XFlash 关闭时返回 <see langword="true"/>。</para>
    /// </value>
    public bool IsConnected => !_disposed && Transport.IsConnected
        && _state is not (MtkSessionState.Faulted or MtkSessionState.XFlashShutdownAcknowledged);

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
    /// (port A) must be selected by the caller, not the logging port.
    /// <para>在已打开的 USB 批量端点会话上打开会话（BROM PID 0E8D:0003 或
    /// preloader/DA 0E8D:2000|2001）。BROM/XFlash 命令集在 USB 上与串口一致（mtkclient
    /// 在 bulk 端点使用同一字节协议）；调用方须选定命令接口（port A）而非日志接口。</para>
    /// </summary>
    /// <param name="session">An opened USB device session bound to the command interface.
    /// 已打开并绑定命令接口的 USB 设备会话。</param>
    /// <param name="cancellationToken">The operation cancellation token. 取消令牌。</param>
    /// <returns>An initialized bootloader session which owns the USB transport.
    /// 拥有 USB 传输的已初始化 bootloader 会话。</returns>
    internal static async Task<MtkSession> OpenUsbAsync(UotanStudio.Comm.Transports.Usb.Abstractions.IUsbDeviceSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var usbSession = new MtkSession(new UsbDeviceTransport(session));
        try
        {
            await usbSession.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return usbSession;
        }
        catch
        {
            usbSession.Dispose();
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
            try
            {
                foreach (var value in new byte[] { 0xA0, 0x0A, 0x50, 0x05 })
                {
                    await WriteAsync([value], _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
                    var reply = await ReadAsync(1, _handshakeTimeoutMs, cancellationToken).ConfigureAwait(false);
                    if (reply[0] != (byte)(value ^ 0xFF))
                    {
                        throw new MtkProtocolException("The boot handshake echo did not match.");
                    }
                }

                await EchoAsync([0xFD], cancellationToken).ConfigureAwait(false);
                var hardware = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
                await EchoAsync([0xFC], cancellationToken).ConfigureAwait(false);
                var versions = await ReadAsync(8, _timeoutMs, cancellationToken).ConfigureAwait(false);
                await EchoAsync([0xD8], cancellationToken).ConfigureAwait(false);
                var configuration = await ReadAsync(6, _timeoutMs, cancellationToken).ConfigureAwait(false);
                CheckStatus(BinaryPrimitives.ReadUInt16BigEndian(configuration.AsSpan(4)), "target configuration query");

                ThrowIfDisposed();
                DeviceInfo = new MtkDeviceInfo(
                    BinaryPrimitives.ReadUInt16BigEndian(hardware),
                    BinaryPrimitives.ReadUInt16BigEndian(versions),
                    BinaryPrimitives.ReadUInt16BigEndian(versions.AsSpan(2)),
                    BinaryPrimitives.ReadUInt16BigEndian(versions.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32BigEndian(configuration));
                _state = MtkSessionState.Bootloader;
                return DeviceInfo;
            }
            catch
            {
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
    /// Uploads one explicitly selected, word-aligned DA image including its signature bytes.
    /// Authentication failures are reported; no image patching or fallback agent is attempted.
    /// <para>上传一个显式选定、按字对齐的 DA 镜像（含其签名字节）。认证失败会被上报；
    /// 不进行镜像修补，也不尝试任何回退代理。</para>
    /// </summary>
    /// <param name="image">
    /// A readable stream positioned at the agent image.
    /// <para>定位到代理镜像起始处、可读的流。</para>
    /// </param>
    /// <param name="loadAddress">
    /// The validated 32-bit target load address supplied by the device profile.
    /// <para>由设备配置提供、已校验的 32 位目标加载地址。</para>
    /// </param>
    /// <param name="length">
    /// The total image length, including the signature, limited to 64 MiB.
    /// <para>镜像总长度（含签名），上限为 64 MiB。</para>
    /// </param>
    /// <param name="signatureLength">
    /// The signature suffix length included in the image.
    /// <para>镜像中包含的签名后缀长度。</para>
    /// </param>
    /// <param name="progress">
    /// Optional cumulative uploaded-byte progress.
    /// <para>可选的累计已上传字节进度。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The operation cancellation token.
    /// <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the device confirms the uploaded agent checksum and the session advances to the agent-uploaded stage.
    /// <para>在设备确认已上传代理的校验和、会话推进到代理已上传阶段后完成的任务。</para>
    /// </returns>
    public Task UploadDownloadAgentAsync(Stream image, uint loadAddress, int length, int signatureLength = 0,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => UploadDownloadAgentCoreAsync(image, loadAddress, length, signatureLength, progress, null, 0, cancellationToken);

    private async Task UploadDownloadAgentCoreAsync(Stream image, uint loadAddress, int length, int signatureLength,
        IProgress<long>? progress, IMtkBootRomSlaResponder? responder, int slaTimeoutMs, CancellationToken cancellationToken)
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
            try
            {
                await EchoAsync([0xD7], cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async(loadAddress, cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async((uint)length, cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async((uint)signatureLength, cancellationToken).ConfigureAwait(false);
                var requestStatus = BinaryPrimitives.ReadUInt16BigEndian(
                    await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
                if (requestStatus == 0x1D0D && responder is not null)
                {
                    await RespondToBootRomSlaOwnedAsync(responder, slaTimeoutMs, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    CheckStatus(requestStatus, "DA upload request");
                }

                var buffer = new byte[1024];
                ushort checksum = 0;
                var uploaded = 0;
                while (uploaded < length)
                {
                    var count = Math.Min(buffer.Length, length - uploaded);
                    await image.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    for (var offset = 0; offset < count; offset += 2)
                    {
                        checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
                    }

                    await WriteAsync(buffer, count, _timeoutMs, cancellationToken).ConfigureAwait(false);
                    uploaded += count;
                    progress?.Report(uploaded);
                }

                var completion = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
                CheckStatus(BinaryPrimitives.ReadUInt16BigEndian(completion.AsSpan(2)), "DA upload completion");
                if (BinaryPrimitives.ReadUInt16BigEndian(completion) != checksum)
                {
                    throw new MtkProtocolException("The uploaded agent checksum did not match the device response.");
                }

                ThrowIfDisposed();
                _agentAddress = loadAddress;
                _state = MtkSessionState.AgentUploaded;
            }
            catch
            {
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
            catch
            {
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

    private async Task<byte[]> ReadAsync(int count, int timeoutMs, CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
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

            var received = await Transport.ReadAsync(bytes, position, count - position,
                (int)remainingMs, cancellationToken).ConfigureAwait(false);
            if (received <= 0 || received > count - position)
            {
                throw new MtkProtocolException("The boot protocol response was truncated or timed out.");
            }

            position += received;
        }

        return bytes;
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