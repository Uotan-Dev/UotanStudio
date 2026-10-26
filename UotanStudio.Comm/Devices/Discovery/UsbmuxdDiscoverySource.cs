using System.Net.Sockets;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Apple.Usbmuxd;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>
/// Discovers Apple devices through the host usbmuxd daemon.
/// <para>经主机 usbmuxd 守护进程发现 Apple 设备。</para>
/// </summary>
internal sealed class UsbmuxdDiscoverySource : IDeviceDiscoverySource
{
    /// <summary>Gets the discovery source name. <para>获取发现源名称。</para></summary>
    public const string SourceName = "usbmuxd";

    private readonly UsbmuxdClientOptions _options;

    /// <summary>
    /// Creates the discovery source.
    /// <para>创建发现源。</para>
    /// </summary>
    /// <param name="options">Optional daemon endpoint options. <para>可选的守护进程端点配置。</para></param>
    public UsbmuxdDiscoverySource(UsbmuxdClientOptions? options = null) =>
        _options = options ?? UsbmuxdClientOptions.Default;

    /// <summary>Gets the source name. <para>获取源名称。</para></summary>
    public string Name => SourceName;

    /// <summary>
    /// Lists the currently attached devices; a missing or unreachable daemon yields an empty list.
    /// <para>列出当前在线设备；守护进程缺失或不可达时返回空列表。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe. <para>取消探测。</para></param>
    /// <returns>The discovered endpoints. <para>发现的端点。</para></returns>
    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UsbmuxdClient(_options);
            var devices = await Task.Run(() => client.ListDevices(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var epoch = DebugBridgeRoute.Current.Epoch;
            var endpoints = new List<DeviceEndpoint>(devices.Count);
            foreach (var device in devices)
            {
                if (string.IsNullOrWhiteSpace(device.SerialNumber))
                {
                    continue;
                }

                endpoints.Add(new DeviceEndpoint
                {
                    Source = SourceName,
                    Transport = SourceName,
                    Address = device.SerialNumber,
                    Serial = device.SerialNumber,
                    DisplayName = device.SerialNumber,
                    Protocol = "apple",
                    Route = SourceName,
                    RouteEpoch = epoch,
                    State = device.ConnectionType,
                });
            }

            return endpoints;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UsbmuxdException)
        {
            return [];
        }
        catch (SocketException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}