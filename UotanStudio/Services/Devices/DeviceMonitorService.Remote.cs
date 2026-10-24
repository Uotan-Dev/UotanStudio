using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    internal static bool TryNormalizeNetworkAddress(DeviceProtocolKind protocol, string? input, out string address)
    {
        address = string.Empty;
        if (protocol is not (DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc))
        {
            return false;
        }

        var defaultPort = protocol == DeviceProtocolKind.Adb ? DebugBridgeNetworkAddress.DefaultDevicePort : 0;
        return DebugBridgeNetworkAddress.TryNormalize(input, defaultPort, out address) &&
               (protocol != DeviceProtocolKind.Hdc || Encoding.UTF8.GetByteCount(address) < 32);
    }

    /// <summary>
    /// Connects a network target through <c>host:connect:</c> when the ADB server listens, or by a direct
    /// TCP handshake with the daemon when it does not. An ADB bare host uses the conventional <c>5555</c>
    /// port; HDC always requires an explicit port because its daemon port is dynamic.
    /// <para>ADB server 在监听时经 <c>host:connect:</c> 连接网络目标，否则与守护进程直接完成 TCP 握手。
    /// ADB 裸主机使用约定的 <c>5555</c> 端口；HDC 因守护进程端口是动态的，始终要求显式端口。</para>
    /// </summary>
    /// <param name="protocol">The network protocol of the target.<para>目标所属的网络协议。</para></param>
    /// <param name="address">An ADB host or host:port, or an HDC host:port.<para>ADB 的主机或主机:端口，或 HDC 的主机:端口。</para></param>
    /// <param name="cancellationToken">Cancels the connection attempt.<para>取消连接尝试。</para></param>
    public async Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeNetworkAddress(protocol, address, out var target))
        {
            throw new ArgumentException(
                protocol switch
                {
                    DeviceProtocolKind.Hdc =>
                        "An HDC network address requires an explicit host:port because the daemon port is dynamic.",
                    DeviceProtocolKind.Adb => "A valid ADB device host or host:port address is required.",
                    _ => "A valid ADB or HDC network address is required.",
                },
                nameof(address));
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
                _verifiedManualHdcTargets.Add(target);
            }
            SavePairedNetworkTargets();
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
    /// Completes one direct handshake with a network target while no ADB server listens. A plaintext handshake
    /// that the peer rejects with an explicit TLS-upgrade request is retried once over TLS, and the registered
    /// target records that requirement so later opens use the secure transport directly. Other failures are
    /// rethrown without a second attempt.
    /// <para>在无 ADB server 时对网络目标完成一次直接握手；对端明确要求 TLS 升级的明文握手会改用 TLS 重试一次，
    /// 并把该要求记录到手动目标，后续打开直接走安全传输。其他失败不重试，向上抛出底层原因。</para>
    /// </summary>
    /// <param name="target">A <c>host:port</c> endpoint to attach.<para>要登记的 <c>host:port</c> 端点。</para></param>
    private void AttachDirectNetworkTarget(string target)
    {
        if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var host, out var port))
        {
            throw new IOException("A direct network target needs a host:port address.");
        }

        bool knownSecure;
        lock (_networkTargetsLock)
        {
            knownSecure = _manualNetworkTargets.TryGetValue(target, out var requiresTls) && requiresTls;
        }

        var (session, requiresTlsAfterConnect) = ConnectDirectNetworkSession(host, port, knownSecure);
        session.Dispose();
        lock (_networkTargetsLock)
        {
            _manualNetworkTargets[target] = requiresTlsAfterConnect;
            _verifiedManualNetworkTargets.Add(target);
        }
        SavePairedNetworkTargets();
    }

    /// <summary>
    /// Connects once to a direct ADB endpoint, switching to TLS exactly when the peer requests it. A target
    /// already known to require TLS goes straight to the secure transport.
    /// <para>连接一次直连 ADB 端点，仅在对端要求时改用 TLS。已知需要 TLS 的目标直接走安全传输。</para>
    /// </summary>
    /// <param name="host">The target host.<para>目标主机。</para></param>
    /// <param name="port">The target TCP port.<para>目标 TCP 端口。</para></param>
    /// <param name="knownSecure">Whether the remembered target already requires TLS.
    /// <para>已记住的目标是否已经需要 TLS。</para></param>
    /// <returns>The connected session and whether it requires TLS on later opens.
    /// <para>已连接的会话，以及后续打开时是否需要 TLS。</para></returns>
    /// <exception cref="IOException">The plaintext and TLS handshakes both failed.
    /// <para>明文与 TLS 握手都失败。</para></exception>
    private (IAdbDeviceSession Session, bool RequiresTls) ConnectDirectNetworkSession(string host, int port,
        bool knownSecure)
    {
        if (knownSecure)
        {
            return (OpenConnectedNetworkSession(host, port, isSecure: true), true);
        }

        try
        {
            return (OpenConnectedNetworkSession(host, port, isSecure: false), false);
        }
        catch (Exception ex) when (IsTlsRequiredFailure(ex))
        {
            return (OpenConnectedNetworkSession(host, port, isSecure: true), true);
        }
    }

    /// <summary>
    /// Reports whether the failed handshake was a plaintext connection to a daemon that requires TLS, including
    /// the case where the connection layer wraps that cause in a handshake timeout.
    /// <para>判断失败握手是否为“对端要求 TLS 的明文连接”，包括连接层把该原因包进握手超时的情况。</para>
    /// </summary>
    /// <param name="exception">The failure to inspect, including its inner causes.
    /// <para>待检查的失败及其内部原因。</para></param>
    /// <returns><see langword="true"/> when a TLS-required cause is present.
    /// <para>存在“要求 TLS”的原因时为 <see langword="true"/>。</para></returns>
    internal static bool IsTlsRequiredFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AdbTlsRequiredException)
            {
                return true;
            }
        }

        return false;
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

            _verifiedManualNetworkTargets.Remove(target);
        }
        SavePairedNetworkTargets();
        return true;
    }

    /// <summary>
    /// Persists the remembered ADB and HDC network targets after every change so a restart can re-discover
    /// each endpoint. The HDC list is stored separately because its transport carries no TLS flag.
    /// <para>每次变更后持久化记住的 ADB 与 HDC 网络目标，使重启后能重新发现各端点。HDC 列表单独保存，
    /// 因为其传输没有 TLS 标记。</para>
    /// </summary>
    private void SavePairedNetworkTargets()
    {
        List<PairedNetworkTarget> adbSnapshot;
        List<string> hdcSnapshot;
        lock (_networkTargetsLock)
        {
            adbSnapshot = [.. _manualNetworkTargets
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(64)
                .Select(entry => new PairedNetworkTarget(entry.Key, entry.Value))];
            hdcSnapshot = [.. _manualHdcTargets
                .Order(StringComparer.Ordinal)
                .Take(64)];
        }

        SettingsPersistenceService.Instance.UpdatePairedNetworkTargets(adbSnapshot);
        SettingsPersistenceService.Instance.UpdatePairedHdcNetworkTargets(hdcSnapshot);
    }

    /// <summary>
    /// Restores the remembered ADB and HDC network targets from settings, with a bound on each list.
    /// <para>从设置恢复上次记住的 ADB 与 HDC 网络目标；每个列表都设上限。</para>
    /// </summary>
    private void RestorePairedNetworkTargets()
    {
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        foreach (var target in settings.PairedNetworkTargets.Take(64))
        {
            if (string.IsNullOrWhiteSpace(target.Address))
            {
                continue;
            }

            lock (_networkTargetsLock)
            {
                _manualNetworkTargets[target.Address] = target.RequiresTls;
            }
        }

        foreach (var target in settings.PairedHdcNetworkTargets.Take(64))
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            lock (_networkTargetsLock)
            {
                _manualHdcTargets.Add(target);
            }
        }
    }

    /// <summary>
    /// Manually registered ADB network targets, restored from settings or added in this session; the value
    /// reports whether the target requires TLS (a secure QR-paired endpoint).
    /// <para>手动登记的 ADB 网络目标，来自设置恢复或本会话新增；值表示该目标是否需要 TLS
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
    private readonly HashSet<string> _verifiedManualNetworkTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _verifiedManualHdcTargets = new(StringComparer.Ordinal);

    /// <summary>
    /// Manually registered HDC network targets, restored from settings or added in this session.
    /// <para>手动登记的 HDC 网络目标，来自设置恢复或本会话新增。</para>
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
        bool removed;
        lock (_networkTargetsLock)
        {
            removed = _manualHdcTargets.Remove(target);
            _verifiedManualHdcTargets.Remove(target);
        }

        if (removed)
        {
            SavePairedNetworkTargets();
        }

        return removed;
    }

    /// <summary>
    /// Reports whether a remembered manual ADB target was reached successfully in this session. Restored and
    /// newly paired targets stay unverified until their first successful open.
    /// <para>报告记住的手动 ADB 目标是否在本会话成功连接过。恢复的目标与刚配对的目标在首次成功打开前保持未验证。</para>
    /// </summary>
    /// <param name="target">The remembered <c>host:port</c> value.<para>已记住的 <c>host:port</c> 值。</para></param>
    /// <returns><see langword="true"/> when this session opened the target successfully.
    /// <para>本会话成功打开过该目标时为 <see langword="true"/>。</para></returns>
    internal bool IsManualNetworkTargetVerified(string target)
    {
        lock (_networkTargetsLock)
        {
            return _verifiedManualNetworkTargets.Contains(target);
        }
    }

    /// <summary>
    /// Reports whether a remembered manual HDC target was reached successfully in this session.
    /// <para>报告记住的手动 HDC 目标是否在本会话成功连接过。</para>
    /// </summary>
    /// <param name="target">The remembered <c>host:port</c> value.<para>已记住的 <c>host:port</c> 值。</para></param>
    /// <returns><see langword="true"/> when this session opened the target successfully.
    /// <para>本会话成功打开过该目标时为 <see langword="true"/>。</para></returns>
    internal bool IsManualHdcTargetVerified(string target)
    {
        lock (_networkTargetsLock)
        {
            return _verifiedManualHdcTargets.Contains(target);
        }
    }

    /// <summary>
    /// Maps a manual target's verification state to the device-list state vocabulary. An unverified target is
    /// shown as offline but stays in the list instead of disappearing.
    /// <para>把手动目标的验证状态映射为设备列表状态词汇。未验证目标显示为离线，但仍保留在列表中而不是消失。</para>
    /// </summary>
    /// <param name="verified">Whether the target was opened successfully in this session.
    /// <para>该目标是否在本会话成功打开。</para></param>
    /// <returns><c>device</c> when verified, otherwise <c>offline</c>.
    /// <para>已验证时为 <c>device</c>，否则为 <c>offline</c>。</para></returns>
    internal static string ManualNetworkTargetState(bool verified) => verified ? "device" : "offline";

    /// <summary>
    /// Marks a manual ADB target as verified after a successful direct open.
    /// <para>直连打开成功后把手动 ADB 目标标记为已验证。</para>
    /// </summary>
    /// <param name="target">The <c>host:port</c> value to mark.<para>要标记的 <c>host:port</c> 值。</para></param>
    internal void MarkManualNetworkTargetVerified(string target)
    {
        lock (_networkTargetsLock)
        {
            _verifiedManualNetworkTargets.Add(target);
        }
    }

    /// <summary>
    /// Marks a manual HDC target as verified after a successful direct open.
    /// <para>直连打开成功后把手动 HDC 目标标记为已验证。</para>
    /// </summary>
    /// <param name="target">The <c>host:port</c> value to mark.<para>要标记的 <c>host:port</c> 值。</para></param>
    internal void MarkManualHdcTargetVerified(string target)
    {
        lock (_networkTargetsLock)
        {
            _verifiedManualHdcTargets.Add(target);
        }
    }

    /// <summary>
    /// The deadline for re-browsing the secure connect port by IP address after a failed handshake.
    /// <para>握手失败后按 IP 重新浏览安全连接端口的等待上限。</para>
    /// </summary>
    private static readonly TimeSpan RediscoverSecurePortTimeout = TimeSpan.FromSeconds(3);
}