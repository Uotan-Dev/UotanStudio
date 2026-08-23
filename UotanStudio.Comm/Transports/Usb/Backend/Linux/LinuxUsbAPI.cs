using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal static partial class LinuxUsbAPI
{
    public const string Libc = "libc";

    [StructLayout(LayoutKind.Sequential)]
    public struct Usbdevfs_bulktransfer
    {
        public uint Ep;
        public uint Len;
        public uint Timeout;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Usbdevfs_ctrltransfer
    {
        public byte BRequestType;
        public byte BRequest;
        public ushort WValue;
        public ushort WIndex;
        public ushort WLength;
        public uint Timeout;
        public IntPtr Data;
    }

    public const uint USBDEVFS_BULK = 0xC0105502;
    public static uint USBDEVFS_BULK_X86_64 = 0xC0185502;
    public static uint USBDEVFS_BULK_X86 = 0xC0105502;

    public static uint USBDEVFS_CLAIMINTERFACE = 0x8004550F;
    public static uint USBDEVFS_RELEASEINTERFACE = 0x80045510;
    public static uint USBDEVFS_DISCONNECT = 0x5516;
    public static uint USBDEVFS_RESET = 0x5514;

    public static uint USBDEVFS_CONTROL_X86_64 = 0xC0185500;
    public static uint USBDEVFS_CONTROL_X86 = 0xC0105500;
    public const uint USBDEVFS_CONTROL = 0xC0105500;

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int Flags);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int close(int Fd);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial IntPtr read(int Fd, IntPtr buf, UIntPtr count);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, IntPtr arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref int arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_bulktransfer arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_ctrltransfer arg);

    [StructLayout(LayoutKind.Sequential)]
    public struct Usbdevfs_urb
    {
        public byte Type;
        public byte Endpoint;
        public int Status;
        public uint Flags;
        public IntPtr Buffer;
        public int Buffer_length;
        public int Actual_length;
        public int Start_frame;
        public int Number_of_packets;
        public int Error_count;
        public uint Signr;
        public IntPtr Usercontext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    // URB control ioctls, encoded exactly as in kernel include/uapi/linux/usbdevice_fs.h:
    //   USBDEVFS_SUBMITURB      _IOR('U', 10, struct Usbdevfs_urb)   -> 56B on x86_64, 44B on x86
    //   USBDEVFS_DISCARDURB     _IO('U', 11)                         -> no argument (same both ABIs)
    //   USBDEVFS_REAPURB        _IOW('U', 12, void *)                -> sizeof(void*) = 8/4
    //   USBDEVFS_REAPURBNDELAY  _IOW('U', 13, void *)                -> sizeof(void*) = 8/4
    // ioctl code = (dir << 30) | (size << 16) | ('U' << 8) | nr, with dir READ=2, WRITE=1, NONE=0.
    // Verified: x86_64 SUBMITURB = 0x8038550A (see QEMU linux-user usbfs emulation docs and
    // kernel KASAN reports); the previous 0xC040550A (WRITE|READ, size 0x40) was wrong and
    // made every async URB submit fail with ENOTTY (25).
    public static uint USBDEVFS_SUBMITURB_X86_64 = 0x8038550A;
    public static uint USBDEVFS_SUBMITURB_X86 = 0x802C550A;
    public static uint USBDEVFS_DISCARDURB_X86_64 = 0x0000550B;
    public static uint USBDEVFS_DISCARDURB_X86 = 0x0000550B;
    public static uint USBDEVFS_REAPURB_X86_64 = 0x4008550C;
    public static uint USBDEVFS_REAPURB_X86 = 0x4004550C;
    public static uint USBDEVFS_REAPURBNDELAY_X86_64 = 0x4008550D;
    public static uint USBDEVFS_REAPURBNDELAY_X86 = 0x4004550D;

    public const byte USBDEVFS_URB_TYPE_BULK = 2;
    public const byte USBDEVFS_URB_TYPE_INTERRUPT = 3;
    public const uint USBDEVFS_URB_SHORT_NOT_OK = 0x0002;

    public const short POLLIN = 0x001;
    public const short POLLOUT = 0x004;
    public const short POLLERR = 0x008;
    public const short POLLHUP = 0x010;

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref IntPtr arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int poll(ref PollFd fds, uint nfds, int Timeout);

    public const int O_RDWR = 2;
    public const int O_CLOEXEC = 0x80000;

    [LibraryImport(Libc, SetLastError = true)]
    public static partial IntPtr opendir([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int closedir(IntPtr dirp);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial IntPtr readdir(IntPtr dirp);

    [StructLayout(LayoutKind.Sequential)]
    public struct Dirent64
    {
        public ulong D_ino;
        public long D_off;
        public ushort D_reclen;
        public byte D_type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string D_name;
    }

    public const int EINTR = 4;
    public const int EAGAIN = 11;
    public const int EACCES = 13;
    public const int EBUSY = 16;
    public const int ENODEV = 19;
    public const int EPIPE = 32;
    public const int ESHUTDOWN = 108;
    public const int ETIMEDOUT = 110;
    public const int EPROTO = 71;
}