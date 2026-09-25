using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Implements the fastboot protocol session over a device transport.
/// <para>在设备传输之上实现 fastboot 协议会话。</para>
/// </summary>
public sealed partial class FastbootSession : IProtocolSession
{
    /// <summary>
    /// Frame delimiters used to find the end of an INFO/TEXT payload.
    /// <para>用于查找 INFO/TEXT 负载结尾的帧分隔符。</para>
    /// </summary>
    private static readonly char[] ResponseDelimiters = ['\0', '\r', '\n'];
    private readonly int _timeoutMs;
    private readonly Dictionary<string, string> _varCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _hasSlotCache = new(StringComparer.Ordinal);
    private HashSet<string>? _logicalPartitionsFromMetadata;
    private long? _maxDownloadSize;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying USB session when the transport is USB-based; otherwise, <c>null</c>.
    /// <para>获取底层 USB 会话；传输非 USB 时返回 <c>null</c>。</para>
    /// </summary>
    private UsbDeviceTransport? Usb => Transport as UsbDeviceTransport;

    /// <summary>Gets the protocol name. 获取协议名称。</summary>
    public string ProtocolName => "fastboot";

    /// <summary>Gets the underlying transport. 获取底层传输。</summary>
    public ICommTransport Transport { get; }

    /// <summary>Gets a value indicating whether the session is currently connected. 获取会话是否已连接。</summary>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Creates a new fastboot session over the specified transport.
    /// <para>在指定传输上创建新的 fastboot 会话。</para>
    /// </summary>
    internal FastbootSession(ICommTransport transport, int? timeoutMs = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        var defaultTimeoutMs = Usb?.Session.DefaultTimeoutMs ?? 0;
        _timeoutMs = timeoutMs ?? (defaultTimeoutMs > 0 ? defaultTimeoutMs : FastbootConstants.DefaultResponseTimeoutSeconds * 1000);
    }

    /// <summary>
    /// Sends a raw fastboot command and returns the parsed response.
    /// <para>发送原始 fastboot 命令并返回解析后的响应。</para>
    /// </summary>
    public FastbootResponse ExecuteCommand(string command)
    {
        ThrowIfDisposed();
        SendCommand(command);
        return ReceiveResponse();
    }

    /// <summary>
    /// Sends a getvar query and returns the variable value, or empty on FAIL/timeout
    /// (matching AOSP behavior where an unknown variable yields no value).
    /// <para>发送 getvar 查询并返回变量值，FAIL/超时时返回空（与 AOSP 未知变量无值的行为一致）。</para>
    /// </summary>
    public string GetVariable(string key, bool useCache = true)
    {
        if (useCache && _varCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var response = ExecuteCommand(FastbootConstants.CommandGetVar + key);
        if (response.State is FastbootResponseState.Fail or FastbootResponseState.Timeout)
        {
            return string.Empty;
        }

        var value = response.Info;
        if (useCache)
        {
            _varCache[key] = value;
        }

        return value;
    }

    /// <summary>
    /// Queries all device variables via getvar:all and returns them as a dictionary.
    /// Variables are reported as INFO lines of the form "key: value"; the parsed values
    /// are also stored in the getvar cache.
    /// <para>通过 getvar:all 查询所有设备变量并以字典形式返回。变量以 "key: value" 形式的
    /// INFO 行上报；解析结果同时写入 getvar 缓存。</para>
    /// </summary>
    public Dictionary<string, string> GetAllVariables()
    {
        var response = ExecuteCommand(FastbootConstants.CommandGetVarAll);
        response.ThrowIfFailed();

        _varCache.Clear();
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in response.InfoLines)
        {
            var colonIdx = line.LastIndexOf(':');
            if (colonIdx <= 0)
            {
                continue;
            }

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].TrimStart();
            if (!dict.ContainsKey(key))
            {
                dict[key] = value;
                _varCache[key] = value;
            }
        }
        return dict;
    }

    /// <summary>
    /// Tries to query a device variable, returning <c>false</c> on any transport failure.
    /// <para>尝试查询设备变量，传输失败时返回 <c>false</c>。</para>
    /// </summary>
    private bool TryGetVar(string key, out string value)
    {
        try
        {
            value = GetVariable(key, useCache: false);
            return true;
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FastbootSession));
        }
    }

    /// <summary>
    /// Disposes the session and the underlying transport.
    /// <para>释放会话和底层传输。</para>
    /// </summary>
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