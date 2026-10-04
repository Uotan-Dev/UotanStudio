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
    /// <para>连接网络调试目标。ADB server 在监听时走 <c>host:connect:</c>；server 不在时改为直接对 adbd
    /// 的 TCP 端口握手并登记为手动网络端点。省略端口时两条路径都按 5555。</para>
    /// Connects a network target: through <c>host:connect:</c> while the ADB server listens, otherwise by
    /// handshaking the daemon's TCP port directly and registering a manual network endpoint. A bare host
    /// applies port 5555 on both paths.
    /// </summary>
    public async Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeNetworkAddress(protocol, address, out var target))
        {
            throw new ArgumentException("A valid ADB or HDC device host:port address is required.", nameof(address));
        }

        if (protocol == DeviceProtocolKind.Hdc)
        {
            using var hdc = new HdcServerClient(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
            await hdc.ConnectTargetAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
            await ForceRefreshAsync().ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (DebugBridgeRoute.AdbServerAvailable)
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
    /// <para>在无 ADB server 时对网络目标完成一次直接握手，成功后登记为手动网络端点；失败时向上抛出底层原因。</para>
    /// Completes one direct handshake with a network target while no ADB server listens, registering a manual
    /// network endpoint on success and rethrowing the underlying reason otherwise.
    /// </summary>
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
        _manualNetworkTargets[target] = false;
        SavePairedNetworkTargets();
    }

    /// <summary>
    /// <para>移除手动登记的网络目标，返回该目标此前是否由本会话直接持有。</para>
    /// Removes a manually registered network target and reports whether this session held it.
    /// </summary>
    internal bool TryDetachManualNetworkTarget(string target)
    {
        if (!_manualNetworkTargets.Remove(target))
        {
            return false;
        }
        SavePairedNetworkTargets();
        return true;
    }

    /// <summary>
    /// <para>登记或更新目标后立即持久化，重启时才能按 IP 重新发现端口。</para>
    /// Persists the target set after every registration so a restart can re-discover each port by IP.
    /// </summary>
    private void SavePairedNetworkTargets() =>
        SettingsPersistenceService.Instance.UpdatePairedNetworkTargets(
            [.. _manualNetworkTargets.Select(entry => new PairedNetworkTarget(entry.Key, entry.Value))]);

    /// <summary>
    /// <para>从设置恢复上次记住的网络目标；条目数量设上限。</para>
    /// Restores the remembered network targets from settings, with a bound on the entry count.
    /// </summary>
    private void RestorePairedNetworkTargets()
    {
        foreach (var target in SettingsPersistenceService.Instance.LoadSettings().PairedNetworkTargets.Take(64))
        {
            _manualNetworkTargets[target.Address] = target.RequiresTls;
        }
    }

    /// <summary>
    /// <para>本会话在没有 ADB server 时直接登记的网络目标；值表示该目标是否需要 TLS
    /// （扫码直连配对的安全端点）。</para>
    /// Network targets this session registered directly while no ADB server was listening;
    /// the value reports whether the target requires TLS (a secure QR-paired endpoint).
    /// </summary>
    /// <value><para>已登记的 <c>host:port</c> 集合。</para>The registered <c>host:port</c> values.</value>
    public IReadOnlyList<string> ManualNetworkTargets => [.. _manualNetworkTargets.Keys];

    private readonly Dictionary<string, bool> _manualNetworkTargets = new(StringComparer.Ordinal);

    /// <summary>
    /// <para>握手失败后按 IP 重新浏览安全连接端口的等待上限。</para>
    /// The deadline for re-browsing the secure connect port by IP address after a failed handshake.
    /// </summary>
    private static readonly TimeSpan RediscoverSecurePortTimeout = TimeSpan.FromSeconds(3);
}