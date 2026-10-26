using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Comm.Protocols.Apple.Lockdown;

/// <summary>
/// Client for the device lockdownd service: exchanges framed plists over a device stream to query
/// values, pair with the device and start lockdown services. Service streams are opened through the
/// supplied port connector and transparently upgraded to TLS when the device requires it.
/// <para>设备 lockdownd 服务客户端：在设备流上交换组帧属性列表以查询取值、与设备配对并启动
/// lockdown 服务。服务流经由提供的端口连接器打开，并在设备要求时透明升级为 TLS。</para>
/// </summary>
public sealed class LockdownClient : IDisposable
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int HandshakeTimeoutMs = 10_000;
    private const string DefaultLabel = "UotanStudio";

    private readonly Stream _stream;
    private readonly Func<int, CancellationToken, Stream> _portConnector;
    private readonly string _label;
    private LockdownPairingRecord? _pairRecord;
    private X509Certificate2? _tlsCertificate;
    private bool _disposed;

    /// <summary>
    /// Creates a client over an already-connected lockdown stream.
    /// <para>在已连接的 lockdown 流上创建客户端。</para>
    /// </summary>
    /// <param name="stream">The raw stream to lockdownd (usually a mux stream to port 62078). <para>到 lockdownd 的原始流（通常为到 62078 端口的复用流）。</para></param>
    /// <param name="portConnector">Opens a new stream to a device port for service connections. <para>为服务连接打开新的设备端口流。</para></param>
    /// <param name="pairRecord">The stored pair record when the device is already trusted. <para>设备已信任时的既有配对记录。</para></param>
    /// <param name="label">The label lockdownd reports this client under. <para>本客户端在 lockdownd 侧的标签。</para></param>
    public LockdownClient(Stream stream, Func<int, CancellationToken, Stream> portConnector,
        LockdownPairingRecord? pairRecord = null, string label = DefaultLabel)
    {
        _stream = stream;
        _portConnector = portConnector;
        _pairRecord = pairRecord;
        _label = label;
    }

    /// <summary>
    /// Gets or updates the pairing record associated with the device.
    /// <para>获取或更新与设备关联的配对记录。</para>
    /// </summary>
    public LockdownPairingRecord? PairRecord
    {
        get => _pairRecord;
        set => _pairRecord = value;
    }

    /// <summary>
    /// Queries the lockdown protocol type, validating the channel end to end.
    /// <para>查询 lockdown 协议类型，端到端校验通道。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <returns>The reported type, normally <c>com.apple.mobile.lockdown</c>. <para>上报类型，通常为 <c>com.apple.mobile.lockdown</c>。</para></returns>
    /// <exception cref="LockdownException">The device rejected the request. <para>设备拒绝请求时抛出。</para></exception>
    public string QueryType(CancellationToken cancellationToken = default)
    {
        var response = Request(new Dictionary<string, object> { ["Request"] = "QueryType" }, cancellationToken);
        return response.TryGetValue("Type", out var value) && value is string type
            ? type
            : throw new LockdownException("The lockdownd QueryType response does not carry a type.");
    }

    /// <summary>
    /// Reads one lockdown value.
    /// <para>读取一个 lockdown 取值。</para>
    /// </summary>
    /// <param name="domain">The value domain, or <see langword="null"/> for the root domain. <para>取值域；<see langword="null"/> 表示根域。</para></param>
    /// <param name="key">The value key, or <see langword="null"/> for a whole-domain dictionary. <para>取值键；<see langword="null"/> 表示整域字典。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <returns>The value, or <see langword="null"/> when the device does not report it. <para>取值；设备未上报时为 <see langword="null"/>。</para></returns>
    /// <exception cref="LockdownException">The device rejected the request. <para>设备拒绝请求时抛出。</para></exception>
    public object? GetValue(string? domain, string? key, CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object> { ["Request"] = "GetValue" };
        if (!string.IsNullOrEmpty(domain))
        {
            request["Domain"] = domain;
        }

        if (!string.IsNullOrEmpty(key))
        {
            request["Key"] = key;
        }

        var response = Request(request, cancellationToken);
        return response.TryGetValue("Value", out var value) ? value : null;
    }

    /// <summary>
    /// Reads the full lockdown value dictionary, the usual source for device information.
    /// <para>读取完整 lockdown 取值字典，通常是设备信息的来源。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <returns>The full value dictionary. <para>完整取值字典。</para></returns>
    /// <exception cref="LockdownException">The device rejected the request. <para>设备拒绝请求时抛出。</para></exception>
    public Dictionary<string, object> GetAllValues(CancellationToken cancellationToken = default)
    {
        var response = Request(new Dictionary<string, object> { ["Request"] = "GetValue" }, cancellationToken);
        return response.TryGetValue("Value", out var value) && value is Dictionary<string, object> values
            ? values
            : throw new LockdownException("The lockdownd GetValue response does not carry a value dictionary.");
    }

    /// <summary>
    /// Runs the pairing handshake using the record produced by
    /// <see cref="LockdownPairingRecord.CreatePending"/>; the device must be showing the trust dialog,
    /// and the result carries the escrow bag.
    /// <para>使用 <see cref="LockdownPairingRecord.CreatePending"/> 产生的记录执行配对握手；设备必须
    /// 正在显示信任对话框，结果携带 escrow bag。</para>
    /// </summary>
    /// <param name="record">The pending record; its escrow bag is updated in place on success. <para>待配对记录；成功时其 escrow bag 就地更新。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <exception cref="LockdownException">Pairing failed, including the pending-trust case. <para>配对失败（含待信任状态）时抛出。</para></exception>
    public void Pair(LockdownPairingRecord record, CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object>
        {
            ["Request"] = "Pair",
            ["Label"] = _label,
            ["ProtocolVersion"] = "2",
            ["PairRecord"] = record.ToPlist(),
            ["PairingOptions"] = new Dictionary<string, object>
            {
                ["ExtendedPairingErrors"] = true,
            },
        };
        var response = Request(request, cancellationToken);
        if (response.TryGetValue("EscrowBag", out var bag) && bag is byte[] bytes)
        {
            record.EscrowBag = bytes;
        }

        _pairRecord = record;
    }

    /// <summary>
    /// Validates an existing pairing with the device.
    /// <para>校验与设备的既有配对。</para>
    /// </summary>
    /// <param name="record">The record to validate. <para>待校验的记录。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <exception cref="LockdownException">The device rejected the pairing. <para>设备拒绝配对时抛出。</para></exception>
    public void ValidatePair(LockdownPairingRecord record, CancellationToken cancellationToken = default)
    {
        Request(new Dictionary<string, object>
        {
            ["Request"] = "ValidatePair",
            ["Label"] = _label,
            ["PairRecord"] = record.ToPlist(),
        }, cancellationToken);
    }

    /// <summary>
    /// Starts a lockdown service and returns the connected stream, TLS-upgraded when required.
    /// <para>启动 lockdown 服务并返回已连接流；需要时已完成 TLS 升级。</para>
    /// </summary>
    /// <param name="serviceName">The service identifier, for example <see cref="AppleConstants.ServiceAfc"/>. <para>服务标识，例如 <see cref="AppleConstants.ServiceAfc"/>。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消操作。</para></param>
    /// <returns>The service stream. <para>服务流。</para></returns>
    /// <exception cref="LockdownException">The service is unknown or pairing is missing. <para>服务未知或缺少配对时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发时抛出。</para></exception>
    public Stream StartService(string serviceName, CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object>
        {
            ["Request"] = "StartService",
            ["Label"] = _label,
            ["Service"] = serviceName,
        };
        if (_pairRecord?.EscrowBag is { Length: > 0 } escrow)
        {
            request["EscrowBag"] = escrow;
        }

        var response = Request(request, cancellationToken);
        if (!response.TryGetValue("Port", out var portValue) || portValue is not long port)
        {
            throw new LockdownException($"The lockdownd StartService response for {serviceName} has no port.");
        }

        var ssl = response.TryGetValue("EnableServiceSSL", out var sslValue) && sslValue is true;
        var raw = _portConnector((int)port, cancellationToken);
        if (!ssl)
        {
            return raw;
        }

        try
        {
            return UpgradeToTls(raw, cancellationToken);
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _tlsCertificate?.Dispose();
    }

    private Stream UpgradeToTls(Stream raw, CancellationToken cancellationToken)
    {
        var record = _pairRecord
            ?? throw new LockdownException("The service requires TLS but no pair record is available.");
        _tlsCertificate ??= record.BuildTlsClientCertificate();
        var certificate = _tlsCertificate;
        var sslStream = new SslStream(raw, leaveInnerStreamOpen: false);
        try
        {
            sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "lockdownd",
                ClientCertificates = new X509CertificateCollection { certificate },
                EnabledSslProtocols = SslProtocols.None,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
            }, cancellationToken).GetAwaiter().GetResult();
            return sslStream;
        }
        catch
        {
            sslStream.Dispose();
            throw;
        }
    }

    private Dictionary<string, object> Request(Dictionary<string, object> request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = ApplePlist.ToXml(request);
        var framing = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framing, (uint)payload.Length);
        payload.CopyTo(framing, 4);
        WriteAll(framing, cancellationToken);

        var header = new byte[4];
        ReadAll(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxResponseBytes)
        {
            throw new LockdownException($"The lockdownd response length {length} is out of range.");
        }

        var responsePayload = new byte[length];
        ReadAll(responsePayload, cancellationToken);
        if (ApplePlist.Parse(responsePayload) is not Dictionary<string, object> response)
        {
            throw new LockdownException("The lockdownd response is not a dictionary.");
        }

        ThrowIfError(response);
        return response;
    }

    private static void ThrowIfError(Dictionary<string, object> response)
    {
        if (response.TryGetValue("Error", out var error))
        {
            string code;
            string description;
            switch (error)
            {
                case Dictionary<string, object> details:
                    code = details.TryGetValue("Code", out var codeValue) && codeValue is string codeText
                        ? codeText
                        : LockdownException.UnknownErrorCode;
                    description = details.TryGetValue("Description", out var descriptionValue) &&
                        descriptionValue is string descriptionText
                            ? descriptionText
                            : code;
                    break;
                case string errorText:
                    code = errorText;
                    description = errorText;
                    break;
                default:
                    return;
            }

            throw new LockdownException($"lockdownd reported {code}: {description}.", code);
        }

        if (response.TryGetValue("Status", out var status) && status is long statusCode && statusCode != 0)
        {
            throw new LockdownException($"lockdownd reported status {statusCode}.");
        }
    }

    private void WriteAll(byte[] bytes, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < bytes.Length)
        {
            _stream.WriteAsync(bytes.AsMemory(written), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            written = bytes.Length;
        }
    }

    private void ReadAll(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        var deadline = Environment.TickCount64 + HandshakeTimeoutMs;
        while (read < buffer.Length)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("The lockdown exchange timed out.");
            }

            var count = _stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (count <= 0)
            {
                throw new LockdownException("The lockdown stream closed during a read.");
            }

            read += count;
        }
    }
}