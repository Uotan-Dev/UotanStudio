using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal static partial class LinuxUsbAPI
{
    public const string Libc = "libc";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Usbdevfs_bulktransfer
    {
        public uint Ep;
        public uint Len;
        public uint Timeout;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Usbdevfs_ctrltransfer
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

    /// <summary>
    /// Ioctl request code for USBDEVFS_CLEAR_HALT, encoded as <c>_IOR('U', 21, unsigned int)</c> per
    /// <c>linux/usbdevice_fs.h</c>; the argument is the endpoint address passed as an unsigned int,
    /// which reuses the existing <c>ioctl(int, UIntPtr, ref int)</c> declaration.
    /// <para>USBDEVFS_CLEAR_HALT 的 ioctl 请求码，按 <c>linux/usbdevice_fs.h</c> 编码为
    /// <c>_IOR('U', 21, unsigned int)</c>；参数为以无符号整数传递的端点地址，可复用现有的
    /// <c>ioctl(int, UIntPtr, ref int)</c> 声明。</para>
    /// </summary>
    public const uint USBDEVFS_CLEAR_HALT = 0x80045515;

    public static uint USBDEVFS_CONTROL_X86_64 = 0xC0185500;
    public static uint USBDEVFS_CONTROL_X86 = 0xC0105500;
    public const uint USBDEVFS_CONTROL = 0xC0105500;

    public const uint USBDEVFS_CONNINFO_EX = 0x80185520;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Usbdevfs_conninfo_ex
    {
        public uint Size;
        public uint BusNumber;
        public uint DeviceAddress;
        public uint Speed;
        public byte PortCount;
        public byte Port0;
        public byte Port1;
        public byte Port2;
        public byte Port3;
        public byte Port4;
        public byte Port5;
        public byte Port6;
    }

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_conninfo_ex arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int Flags);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int close(int Fd);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial IntPtr read(int Fd, IntPtr buf, UIntPtr count);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial IntPtr pread(int Fd, IntPtr buf, UIntPtr count, long offset);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, IntPtr arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref int arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_bulktransfer arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_ctrltransfer arg);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Usbdevfs_urb
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
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

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
    internal struct Dirent64
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