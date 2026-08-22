using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal static class LinuxUsbAPI
{
    public const string Libc = "libc";

    [StructLayout(LayoutKind.Sequential)]
    public struct usbdevfs_bulktransfer
    {
        public uint ep;
        public uint len;
        public uint timeout;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct usbdevfs_ctrltransfer
    {
        public byte bRequestType;
        public byte bRequest;
        public ushort wValue;
        public ushort wIndex;
        public ushort wLength;
        public uint timeout;
        public IntPtr data;
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

    [DllImport(Libc, SetLastError = true)]
    public static extern int open(string pathname, int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern IntPtr read(int fd, IntPtr buf, UIntPtr count);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, UIntPtr request, IntPtr arg);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, UIntPtr request, ref int arg);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, UIntPtr request, ref usbdevfs_bulktransfer arg);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, UIntPtr request, ref usbdevfs_ctrltransfer arg);

    [StructLayout(LayoutKind.Sequential)]
    public struct usbdevfs_urb
    {
        public byte type;
        public byte endpoint;
        public int status;
        public uint flags;
        public IntPtr buffer;
        public int buffer_length;
        public int actual_length;
        public int start_frame;
        public int number_of_packets;
        public int error_count;
        public uint signr;
        public IntPtr usercontext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    // URB control ioctls, encoded exactly as in kernel include/uapi/linux/usbdevice_fs.h:
    //   USBDEVFS_SUBMITURB      _IOR('U', 10, struct usbdevfs_urb)   -> 56B on x86_64, 44B on x86
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

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, UIntPtr request, ref IntPtr arg);

    [DllImport(Libc, SetLastError = true)]
    public static extern int poll(ref PollFd fds, uint nfds, int timeout);

    public const int O_RDWR = 2;
    public const int O_CLOEXEC = 0x80000;

    [DllImport(Libc, SetLastError = true)]
    public static extern IntPtr opendir(string name);

    [DllImport(Libc, SetLastError = true)]
    public static extern int closedir(IntPtr dirp);

    [DllImport(Libc, SetLastError = true)]
    public static extern IntPtr readdir(IntPtr dirp);

    [StructLayout(LayoutKind.Sequential)]
    public struct Dirent64
    {
        public ulong d_ino;
        public long d_off;
        public ushort d_reclen;
        public byte d_type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string d_name;
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
