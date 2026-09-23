using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>
/// An <see cref="ICommTransport"/> implementation for the HSUART protocol: a serial link
/// wrapped in COBS packet framing with CRC-16, version negotiation, and ACK/NAK semantics.
/// <para>HSUART 协议的 <see cref="ICommTransport"/> 实现：以 COBS 数据包封装（含 CRC-16、
/// 版本协商、ACK/NAK 语义）包裹串行链路。</para>
/// Every payload write is framed as a protocol packet and acknowledged by the device before
/// returning; every read returns the payload of the next received protocol packet.
/// <para>每次负载写入都会被封装为协议数据包并在返回前等待设备确认；每次读取返回下一个
/// 收到的协议数据包的负载。</para>
/// </summary>
/// <remarks>
/// Initializes a new <see cref="HsuartTransport"/> with the specified serial port parameters.
/// <para>使用指定的串口参数初始化 <see cref="HsuartTransport"/> 的新实例。</para>
/// </remarks>
public sealed partial class HsuartTransport : ICommTransport
{
    private bool _disposed;
    private int _disposeStarted;
    private int _disconnectRequests;
    private int _disconnectGeneration;
    private bool _versionNegotiated;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sessionCtsLock = new();
    private CancellationTokenSource _sessionCts = new();
    private readonly HsuartFrameParser _frameParser = new();
    private readonly Queue<byte[]> _receivedFrames = [];
    private readonly Queue<byte[]> _receivedPayloads = [];
    private byte[]? _pendingPayload;
    private int _pendingPayloadOffset;

    private const int MaxRetries = 3;
    private const int VersionNegotiationTimeoutMs = 5000;
    private readonly bool _rawAcknowledgementMode;

    /// <summary>Creates a transport using the protocol-compatible raw ACK mode.</summary>
    public HsuartTransport(
        string portName,
        int baudRate = 3000000,
        Parity parity = Parity.None,
        int dataBits = 8,
        StopBits stopBits = StopBits.One,
        Handshake handshake = Handshake.None)
        : this(portName, baudRate, parity, dataBits, stopBits, handshake, true)
    {
    }

    /// <summary>Creates a transport with explicitly configured raw ACK handling.</summary>
    public HsuartTransport(
        string portName,
        int baudRate,
        Parity parity,
        int dataBits,
        StopBits stopBits,
        Handshake handshake,
        bool rawAcknowledgementMode)
    {
        Serial = new SerialTransport(portName, baudRate, parity, dataBits, stopBits, handshake);
        _rawAcknowledgementMode = rawAcknowledgementMode;
    }

    /// <summary>
    /// Gets a value indicating whether the underlying serial transport is connected.
    /// <para>获取底层串口传输是否已连接。</para>
    /// </summary>
    public bool IsConnected => Serial.IsConnected && Volatile.Read(ref _versionNegotiated) &&
        Volatile.Read(ref _disconnectRequests) == 0 && Volatile.Read(ref _disposeStarted) == 0;

    /// <summary>
    /// Gets or sets the default timeout in milliseconds for read and write operations.
    /// <para>获取或设置读写操作的默认超时时间（毫秒）。</para>
    /// </summary>
    public int DefaultTimeoutMs
    {
        get => Serial.DefaultTimeoutMs;
        set => Serial.DefaultTimeoutMs = value;
    }

    /// <summary>
    /// Gets the underlying serial transport, exposed for callers that need raw serial access.
    /// <para>获取底层串口传输，供需要原始串口访问的调用方使用。</para>
    /// </summary>
    public SerialTransport Serial { get; }

}