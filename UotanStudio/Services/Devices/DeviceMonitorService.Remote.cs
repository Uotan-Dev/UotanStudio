using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    internal static bool TryNormalizeNetworkAddress(DeviceProtocolKind protocol, string? input, out string address)
    {
        address = string.Empty;
        return protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc &&
               DebugBridgeNetworkAddress.TryNormalize(input, DebugBridgeNetworkAddress.DefaultDevicePort, out address) &&
               (protocol != DeviceProtocolKind.Hdc || Encoding.UTF8.GetByteCount(address) < 32);
    }

    /// <summary>
    /// Connects a network target through <c>host:connect:</c> when the ADB server listens, or by a direct
    /// TCP handshake with the daemon when it does not.
    /// <para>ADB server 在监听时经 <c>host:connect:</c> 连接网络目标，否则与守护进程直接完成 TCP 握手。</para>
    /// </summary>
    /// <param name="protocol">The network protocol of the target.<para>目标所属的网络协议。</para></param>
    /// <param name="address">A bare address or an address with a port to connect.<para>省略端口或写明端口的待连接地址。</para></param>
    /// <param name="cancellationToken">Cancels the connection attempt.<para>取消连接尝试。</para></param>
    public async Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeNetworkAddress(protocol, address, out var target))
        {
            throw new ArgumentException("A valid ADB or HDC device host:port address is required.", nameof(address));
        }

        if (protocol == DeviceProtocolKind.Hdc)
        {
            if (DebugBridgeRoute.UseHdcServer())
            {
                using var hdc = new HdcServerClient(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
                await hdc.ConnectTargetAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
                await ForceRefreshAsync().ConfigureAwait(false);
                return;
            }

            if (DebugBridgeRoute.Mode == Comm.Routing.DebugBridgeRouteMode.ServerOnly)
            {
                throw new IOException("The ServerOnly route mode requires a listening HDC server.");
            }

            if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var hdcHost, out var hdcPort))
            {
                throw new IOException("A direct HDC network target needs a host:port address.");
            }

            var hdcSession = _controller.OpenNetworkHdcSession(hdcHost, hdcPort);
            try
            {
                hdcSession.Connect();
            }
            catch
            {
                hdcSession.Dispose();
                throw;
            }
            hdcSession.Dispose();
            lock (_networkTargetsLock)
            {
                _manualHdcTargets.Add(target);
            }
            await ForceRefreshAsync().ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (DebugBridgeRoute.Mode == Comm.Routing.DebugBridgeRouteMode.ServerOnly &&
            !DebugBridgeRoute.UseAdbServer())
        {
            throw new IOException("The ServerOnly route mode requires a listening ADB server.");
        }

        if (DebugBridgeRoute.UseAdbServer())
        {
            await AdbServerServices.ConnectAsync(target, DebugBridgeEndpoints.ServerHost,
                DebugBridgeEndpoints.AdbServerPort, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            AttachDirectNetworkTarget(target);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Completes one direct handshake with a network target while no ADB server listens, registering a manual
    /// network endpoint on success and rethrowing the underlying reason otherwise.
    /// <para>在无 ADB server 时对网络目标完成一次直接握手，成功后登记为手动网络端点；失败时向上抛出底层原因。</para>
    /// </summary>
    /// <param name="target">A <c>host:port</c> endpoint to attach.<para>要登记的 <c>host:port</c> 端点。</para></param>
    private void AttachDirectNetworkTarget(string target)
    {
        if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var host, out var port))
        {
            throw new IOException("A direct network target needs a host:port address.");
        }

        var session = _controller.OpenNetworkAdbSession(host, port);
        try
        {
            session.Connect();
        }
        catch (Exception)
        {
            session.Dispose();
            throw;
        }
        session.Dispose();
        lock (_networkTargetsLock)
        {
            _manualNetworkTargets[target] = false;
        }
        SavePairedNetworkTargets();
    }

    /// <summary>
    /// Removes a manually registered network target and reports whether this session held it.
    /// <para>移除手动登记的网络目标，返回该目标是否由本会话直接持有。</para>
    /// </summary>
    /// <param name="target">A manually registered <c>host:port</c> value.<para>手动登记的 <c>host:port</c> 值。</para></param>
    /// <returns><see langword="true"/> when this session registered the target.<para>该目标由本会话登记时为 <see langword="true"/>。</para></returns>
    internal bool TryDetachManualNetworkTarget(string target)
    {
        lock (_networkTargetsLock)
        {
            if (!_manualNetworkTargets.Remove(target))
            {
                return false;
            }
        }
        SavePairedNetworkTargets();
        return true;
    }

    /// <summary>
    /// Persists the target set after every registration so a restart can re-discover each port by IP.
    /// <para>登记或更新目标后立即持久化，重启时才能按 IP 重新发现端口。</para>
    /// </summary>
    private void SavePairedNetworkTargets()
    {
        List<PairedNetworkTarget> snapshot;
        lock (_networkTargetsLock)
        {
            snapshot = [.. _manualNetworkTargets.Select(entry => new PairedNetworkTarget(entry.Key, entry.Value))];
        }
        SettingsPersistenceService.Instance.UpdatePairedNetworkTargets(snapshot);
    }

    /// <summary>
    /// Restores the remembered network targets from settings, with a bound on the entry count.
    /// <para>从设置恢复上次记住的网络目标；条目数量设上限。</para>
    /// </summary>
    private void RestorePairedNetworkTargets()
    {
        foreach (var target in SettingsPersistenceService.Instance.LoadSettings().PairedNetworkTargets.Take(64))
        {
            lock (_networkTargetsLock)
            {
                _manualNetworkTargets[target.Address] = target.RequiresTls;
            }
        }
    }

    /// <summary>
    /// Network targets this session registered directly while no ADB server was listening;
    /// the value reports whether the target requires TLS (a secure QR-paired endpoint).
    /// <para>本会话在没有 ADB server 时直接登记的网络目标；值表示该目标是否需要 TLS
    /// （扫码直连配对的安全端点）。</para>
    /// </summary>
    /// <value>The registered <c>host:port</c> values. <para>已登记的 <c>host:port</c> 集合。</para></value>
    public IReadOnlyList<string> ManualNetworkTargets
    {
        get
        {
            lock (_networkTargetsLock)
            {
                return [.. _manualNetworkTargets.Keys];
            }
        }
    }

    private readonly Lock _networkTargetsLock = new();
    private readonly Dictionary<string, bool> _manualNetworkTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manualHdcTargets = new(StringComparer.Ordinal);

    /// <summary>
    /// HDC network targets this session registered directly while no hdc server was listening.
    /// <para>本会话在没有 hdc server 时直接登记的 HDC 网络目标。</para>
    /// </summary>
    /// <value>The registered HDC <c>host:port</c> values. <para>已登记的 HDC <c>host:port</c> 集合。</para></value>
    public IReadOnlyList<string> ManualHdcTargets
    {
        get
        {
            lock (_networkTargetsLock)
            {
                return [.. _manualHdcTargets];
            }
        }
    }

    internal bool TryDetachManualHdcTarget(string target)
    {
        lock (_networkTargetsLock)
        {
            return _manualHdcTargets.Remove(target);
        }
    }

    /// <summary>
    /// The deadline for re-browsing the secure connect port by IP address after a failed handshake.
    /// <para>握手失败后按 IP 重新浏览安全连接端口的等待上限。</para>
    /// </summary>
    private static readonly TimeSpan RediscoverSecurePortTimeout = TimeSpan.FromSeconds(3);
}