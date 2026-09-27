using System.Globalization;
using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

internal static partial class MacUsbPortIdentity
{
    internal static string? Observe(IntPtr service)
    {
        if (service == IntPtr.Zero)
        {
            return null;
        }
        var key = IOKitUsbAPI.CFStringCreateWithCString(IntPtr.Zero, "locationID", IOKitUsbAPI.KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var property = IOKitUsbAPI.IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (property == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                if (IOKitUsbAPI.CFGetTypeID(property) != CFNumberGetTypeID() || CFNumberIsFloatType(property))
                {
                    return null;
                }
                long value = 0;
                return CFNumberGetValue(property, 4, ref value) ? Format(value) : null;
            }
            finally
            {
                IOKitUsbAPI.CFRelease(property);
            }
        }
        finally
        {
            IOKitUsbAPI.CFRelease(key);
        }
    }

    internal static string? Format(long value) =>
        value is < int.MinValue or > uint.MaxValue or 0
            ? null
            : $"macos-port:{unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture)}";

    internal static bool Matches(string? expected, string? observed) =>
        expected is null || string.Equals(expected, observed, StringComparison.Ordinal);

    [LibraryImport(IOKitUsbAPI.CoreFoundation)]
    private static partial IntPtr CFNumberGetTypeID();

    [LibraryImport(IOKitUsbAPI.CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberIsFloatType(IntPtr number);

    [LibraryImport(IOKitUsbAPI.CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(IntPtr number, int type, ref long value);
}