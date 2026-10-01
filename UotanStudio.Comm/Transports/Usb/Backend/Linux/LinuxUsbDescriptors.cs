using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal static class LinuxUsbDescriptors
{
    internal static byte[]? Read(int fd) => Read((offset, length) => ReadAtOffset(fd, offset, length));

    internal static byte[]? Read(Func<int, int, byte[]> readAtOffset)
    {
        var device = readAtOffset(0, 18);
        if (device.Length != 18 || device[0] != 18 || device[1] != 1 || device[17] == 0)
        {
            return null;
        }
        List<byte[]> parts = [device];
        var offset = 18;
        for (var index = 0; index < device[17]; index++)
        {
            var header = readAtOffset(offset, 9);
            if (header.Length != 9 || header[0] != 9 || header[1] != 2)
            {
                return null;
            }
            var configLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            if (configLength < 9)
            {
                return null;
            }
            var config = readAtOffset(offset, configLength);
            if (config.Length != configLength || !config.AsSpan(0, 9).SequenceEqual(header))
            {
                return null;
            }
            parts.Add(config);
            offset += configLength;
        }
        var descriptor = new byte[offset];
        offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(descriptor, offset);
            offset += part.Length;
        }
        return descriptor;
    }

    private static unsafe byte[] ReadAtOffset(int fd, int offset, int length)
    {
        var buffer = new byte[length];
        fixed (byte* pointer = buffer)
        {
            long count;
            do
            {
                count = pread(fd, (IntPtr)pointer, (UIntPtr)length, offset).ToInt64();
            }
            while (count < 0 && Marshal.GetLastWin32Error() == EINTR);
            return count == length ? buffer : [];
        }
    }
}