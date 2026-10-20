using System.Globalization;
using System.Text;
using static UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal static class LinuxUsbPortIdentity
{
    internal static string? Observe(int fd, string devicePath)
    {
        var info = new Usbdevfs_conninfo_ex();
        return ioctl(fd, USBDEVFS_CONNINFO_EX, ref info) == 0 ? Format(info, devicePath) : null;
    }

    internal static string? Format(Usbdevfs_conninfo_ex info, string devicePath)
    {
        if (info.Size < 24 || info.BusNumber == 0 || info.DeviceAddress is 0 or > 127
            || info.PortCount is 0 or > 7)
        {
            return null;
        }
        var components = devicePath.Split('/');
        if (components.Length < 3
            || !uint.TryParse(components[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var bus)
            || !uint.TryParse(components[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var address)
            || bus != info.BusNumber || address != info.DeviceAddress)
        {
            return null;
        }
        ReadOnlySpan<byte> ports = [info.Port0, info.Port1, info.Port2, info.Port3, info.Port4, info.Port5, info.Port6];
        var portCount = (int)info.PortCount;
        var builder = new StringBuilder(16 + (portCount * 4));
        builder.Append("linux-port:").Append(info.BusNumber.ToString(CultureInfo.InvariantCulture)).Append(':');
        for (var index = 0; index < portCount; index++)
        {
            var port = ports[index];
            if (port == 0)
            {
                return null;
            }

            if (index > 0)
            {
                builder.Append('.');
            }

            builder.Append(port.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    internal static bool Matches(string? expected, string? observed) =>
        expected is null || string.Equals(expected, observed, StringComparison.Ordinal);
}