using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Factory for creating <see cref="RkdlSession"/> instances from USB device sessions.
/// <para>从 USB 设备会话创建 <see cref="RkdlSession"/> 实例的工厂。</para>
/// </summary>
public static class RkdlFactory
{
    /// <summary>
    /// Tries to create an RKDL session for the specified USB transport.
    /// <para>尝试为指定 USB 传输创建 RKDL 会话。</para>
    /// </summary>
    public static RkdlSession? TryCreate(IUsbDeviceSession transport)
    {
        if (transport.DeviceInfo.VendorId != RkdlConstants.RockchipVid) return null;
        return new RkdlSession(transport);
    }

    /// <summary>
    /// Checks if the USB device is a Rockchip bootrom/loader device.
    /// <para>检查 USB 设备是否为 Rockchip bootrom/loader 设备。</para>
    /// </summary>
    public static bool IsRockchipDevice(IUsbDeviceSession transport)
    {
        if (transport.DeviceInfo.VendorId != RkdlConstants.RockchipVid) return false;
        var pid = transport.DeviceInfo.ProductId;
        return pid == 0x350B || pid == 0x320B || pid == 0x330C || pid == 0x320A ||
               pid == 0x330A || pid == 0x350A || pid == 0x300A || pid == 0x350D ||
               pid == 0x350E || pid == 0x330D;
    }
}
