using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Projects backend <see cref="UsbDevice"/> lists into session or info collections, applying optional filters.
/// <para>将后端 <see cref="UsbDevice"/> 列表投影为会话或信息集合，并应用可选过滤器。</para>
/// </summary>
internal static class UsbProviderProjection
{
    /// <summary>
    /// Converts backend devices into <see cref="IUsbDeviceSession"/> instances, disposing those that do not match the filter.
    /// <para>将后端设备转换为 <see cref="IUsbDeviceSession"/> 实例，释放不匹配过滤器的设备。</para>
    /// </summary>
    /// <param name="apiName">The API name associated with the devices. <para>与设备关联的 API 名称。</para></param>
    /// <param name="apiKind">The backend API kind. <para>后端 API 类型。</para></param>
    /// <param name="devices">The list of backend devices. <para>后端设备列表。</para></param>
    /// <param name="filter">An optional filter to apply. <para>可选的过滤器。</para></param>
    /// <returns>A read-only list of sessions that match the filter. <para>匹配过滤器的会话只读列表。</para></returns>
    /// <param name="ensureDirect">Checks server ownership before opening the matching USB device.</param>
    public static IReadOnlyList<IUsbDeviceSession> ToSessions(
        string apiName,
        UsbApiKind apiKind,
        IReadOnlyList<UsbDevice> devices,
        UsbDeviceFilter? filter,
        Action<UsbDeviceInfo>? ensureDirect = null)
    {
        ensureDirect ??= UsbAccessPolicy.RequireDirect;
        var sessions = new List<IUsbDeviceSession>(devices.Count);
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            try
            {
                var info = UsbDeviceInfoFactory.FromBackendDevice(apiName, apiKind, device);
                if (filter is not null && !filter.MatchesBeforeOpen(info))
                {
                    device.Dispose();
                    continue;
                }
                ensureDirect(info);
                if (!device.IsHandleOpen)
                {
                    var openError = device.CreateHandle();
                    if (openError != 0)
                    {
                        UsbTrace.Log(
                            $"Open {info.DeviceKey} failed with backend error {openError}; "
                            + $"interface {info.InterfaceClass:X2}/{info.InterfaceSubClass:X2}/{info.InterfaceProtocol:X2}, "
                            + $"path {info.DevicePath}.");
                        device.Dispose();
                        continue;
                    }
                }
                var session = new UsbDeviceSession(apiName, apiKind, device);
                if (filter is null || filter.Matches(session.DeviceInfo)) sessions.Add(session);
                else session.Dispose();
            }
            catch
            {
                foreach (var opened in sessions) opened.Dispose();
                for (var remaining = index; remaining < devices.Count; remaining++) devices[remaining].Dispose();
                throw;
            }
        }

        return sessions;
    }

    /// <summary>
    /// Converts backend devices into <see cref="UsbDeviceInfo"/> instances, disposing each device after projection.
    /// <para>将后端设备转换为 <see cref="UsbDeviceInfo"/> 实例，投影后释放每个设备。</para>
    /// </summary>
    /// <param name="apiName">The API name associated with the devices. <para>与设备关联的 API 名称。</para></param>
    /// <param name="apiKind">The backend API kind. <para>后端 API 类型。</para></param>
    /// <param name="devices">The list of backend devices. <para>后端设备列表。</para></param>
    /// <param name="filter">An optional filter to apply. <para>可选的过滤器。</para></param>
    /// <returns>A read-only list of device infos that match the filter. <para>匹配过滤器的设备信息只读列表。</para></returns>
    public static IReadOnlyList<UsbDeviceInfo> ToInfos(
        string apiName,
        UsbApiKind apiKind,
        IReadOnlyList<UsbDevice> devices,
        UsbDeviceFilter? filter)
    {
        var infos = new List<UsbDeviceInfo>(devices.Count);

        foreach (var device in devices)
        {
            try
            {
                var info = UsbDeviceInfoFactory.FromBackendDevice(apiName, apiKind, device);
                if (filter == null || filter.Matches(info))
                {
                    infos.Add(info);
                }
            }
            finally
            {
                device.Dispose();
            }
        }

        return infos;
    }
}