using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Mcp.Tools;

/// <summary>Owns device access when MCP runs without the desktop host.</summary>
internal sealed class HeadlessDeviceManagerGateway : IDeviceManagerGateway
{
    private readonly DeviceController _controller;
    private readonly Func<bool> _hdcServerAvailable;

    public HeadlessDeviceManagerGateway()
    {
        _controller = DeviceControllerFactory.CreateDefault();
        _hdcServerAvailable = () => DebugBridgeRoute.UseHdcServer();
    }

    internal HeadlessDeviceManagerGateway(DeviceController controller, Func<bool> hdcServerAvailable)
    {
        _controller = controller;
        _hdcServerAvailable = hdcServerAvailable;
    }

    private static T Complete<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
    private static void Close(DeviceLease lease) => lease.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    private DeviceSnapshot Refresh(CancellationToken cancellationToken = default) =>
        Complete(_controller.RefreshAsync(cancellationToken));

    public IReadOnlyList<UsbDeviceInfo> ListUsbDevices()
    {
        var snapshot = Refresh();
        return snapshot.Endpoints.Where(endpoint => IsRoutableUsb(snapshot, endpoint))
            .Select(endpoint => endpoint.UsbInfo ??
                throw new InvalidDataException("USB endpoint has no inventory details.")).ToArray();
    }

    public IReadOnlyList<(string Serial, string State)> ListAdbTargets()
    {
        var snapshot = Refresh();
        return snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
                (endpoint.Route == "server" || IsRoutableUsb(snapshot, endpoint)))
            .Select(endpoint =>
            {
                var identity = endpoint.Serial ?? endpoint.Address;
                if (endpoint.Route == "server")
                {
                    return (identity, endpoint.State ?? "device");
                }

                try
                {
                    return (identity, RunAdb(identity, _ => "device"));
                }
                catch (Exception)
                {
                    return (identity, "offline");
                }
            }).ToArray();
    }

    public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAdb(serial, session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = session.OpenServiceStream(service);
            return operation(stream);
        }, cancellationToken);
    }

    public void PushAdbFile(string serial, string localPath, string remotePath,
        CancellationToken cancellationToken = default)
    {
        _ = RunAdb(serial, session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sync = session.CreateSyncClient();
            sync.Push(localPath, remotePath);
            return true;
        }, cancellationToken);
    }

    private TResult RunAdb<TResult>(string serial,
        Func<UotanStudio.Comm.Contracts.IAdbDeviceSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var snapshot = Refresh(cancellationToken);
        var endpoint = SelectAdb(snapshot, serial);
        var lease = Complete(_controller.OpenAsync(snapshot, endpoint.Id, DeviceSessionKind.Adb, cancellationToken));
        try
        {
            if (!lease.TryGetSession<UotanStudio.Comm.Contracts.IAdbDeviceSession>(out var session))
            {
                throw new InvalidOperationException("ADB endpoint has no supported session.");
            }

            session.Connect();
            if (!session.IsConnected)
            {
                throw new IOException("ADB authentication did not complete.");
            }

            return operation(session);
        }
        finally
        {
            Close(lease);
        }
    }

    public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var snapshot = Refresh(cancellationToken);
        var endpoints = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "fastboot" &&
            IsRoutableUsb(snapshot, endpoint));
        DeviceEndpoint selected;
        if (string.IsNullOrWhiteSpace(target))
        {
            var candidates = endpoints.Take(2).ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException("Exactly one Fastboot device must be connected.");
            }

            selected = candidates[0];
        }
        else
        {
            var candidates = endpoints.Where(endpoint => MatchesFastbootTarget(endpoint, target)).Take(2).ToArray();
            if (candidates.Length == 0)
            {
                throw new IOException($"No Fastboot endpoint matches target '{target}'.");
            }

            if (candidates.Length > 1)
            {
                throw new IOException($"Fastboot target '{target}' is ambiguous.");
            }

            selected = candidates[0];
        }

        var lease = Complete(_controller.OpenAsync(
            snapshot, selected.Id, DeviceSessionKind.Fastboot, cancellationToken));
        try
        {
            if (!lease.TryGetSession<FastbootSession>(out var session))
            {
                throw new InvalidOperationException("The selected device does not expose Fastboot.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return operation(session);
        }
        finally
        {
            Close(lease);
        }
    }

    /// <summary>
    /// <para>按 serial 或 USB 设备键判断某个 Fastboot 端点是否就是调用方选定的目标。</para>
    /// Decides whether a Fastboot endpoint is the target the caller selected, matching either the
    /// reported serial or the USB device key, which is also the endpoint address.
    /// </summary>
    /// <param name="endpoint"><para>待匹配的 Fastboot 端点。</para>The Fastboot endpoint under test.</param>
    /// <param name="target"><para>调用方给出的 serial 或设备键。</para>The caller-supplied serial or device key.</param>
    /// <returns><para>该端点即为所选目标时为 <see langword="true"/>。</para>
    /// <see langword="true"/> when this endpoint is the selected target.</returns>
    private static bool MatchesFastbootTarget(DeviceEndpoint endpoint, string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        (string.Equals(endpoint.Serial ?? endpoint.Address, target, StringComparison.Ordinal) ||
            string.Equals(endpoint.Address, target, StringComparison.Ordinal) ||
            string.Equals(endpoint.UsbInfo?.DeviceKey, target, StringComparison.Ordinal));

    internal bool IsRoutableUsb(DeviceSnapshot snapshot, DeviceEndpoint endpoint)
        => endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            !(endpoint.Protocol == "adb" && ServerOwns(snapshot, endpoint.Serial)) &&
            !(endpoint.Protocol == "hdc" && _hdcServerAvailable());

    private static bool ServerOwns(DeviceSnapshot snapshot, string? serial)
        => snapshot.Endpoints.Any(endpoint => endpoint.Protocol == "adb" && endpoint.Route == "server" &&
            (string.IsNullOrWhiteSpace(serial) ||
                string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal)));

    internal static DeviceEndpoint SelectAdb(DeviceSnapshot snapshot, string serial)
    {
        var serverMatches = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            endpoint.Route == "server" &&
            string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal))
            .Take(2).ToArray();
        if (serverMatches.Length > 1)
        {
            throw new IOException("ADB server target is ambiguous.");
        }

        if (serverMatches.Length == 1)
        {
            return serverMatches[0];
        }

        var matches = snapshot.Endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            !ServerOwns(snapshot, endpoint.Serial) &&
            (string.Equals(endpoint.Serial ?? endpoint.Address, serial, StringComparison.Ordinal) ||
                string.Equals(endpoint.Address, serial, StringComparison.Ordinal)))
            .Take(2).ToArray();
        if (matches.Length != 1)
        {
            throw new IOException("ADB target is missing or ambiguous.");
        }

        return matches[0];
    }
}