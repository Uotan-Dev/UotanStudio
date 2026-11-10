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
    public static uint USBDEVFS_RESET = 0x5514;

    /// <summary>
    /// <c>_IO('U', 22)</c>: detaches the kernel driver bound to one interface. The kernel handles it only
    /// inside <c>USBDEVFS_IOCTL</c>; a bare ioctl with this number matches no case of the dispatcher and is
    /// rejected with <c>ENOTTY</c>, so it is never sent on its own.
    /// <para><c>_IO('U', 22)</c>：摘下绑定在某接口上的内核驱动。内核只在 <c>USBDEVFS_IOCTL</c> 内部处理它；
    /// 直接用该编号发 ioctl 不会命中分派表中任何分支并被 <c>ENOTTY</c> 拒绝，因此绝不单独发送。</para>
    /// </summary>
    public const uint USBDEVFS_DISCONNECT = 0x5516;

    /// <summary>
    /// <c>_IOWR('U', 18, struct usbdevfs_ioctl)</c>: forwards one interface-scoped request to the driver bound
    /// to that interface, which is how <see cref="USBDEVFS_DISCONNECT"/>, <c>USBDEVFS_CONNECT</c> and
    /// <c>USBDEVFS_DISCONNECT_CLAIM</c> reach the kernel.
    /// <para><c>_IOWR('U', 18, struct usbdevfs_ioctl)</c>：把一个接口范围内的请求转发给绑定该接口的驱动，
    /// <see cref="USBDEVFS_DISCONNECT"/>、<c>USBDEVFS_CONNECT</c> 与 <c>USBDEVFS_DISCONNECT_CLAIM</c>
    /// 正是由此抵达内核。</para>
    /// </summary>
    public const uint USBDEVFS_IOCTL = 0xC0105512;

    /// <summary>
    /// <c>_IOR('U', 27, struct usbdevfs_disconnect_claim)</c>: detaches whichever driver holds the interface
    /// (except <c>usbfs</c> itself) and claims it for this descriptor in one step.
    /// <para><c>_IOR('U', 27, struct usbdevfs_disconnect_claim)</c>：把占用该接口的驱动（<c>usbfs</c> 自身除外）
    /// 摘下并为当前描述符申请该接口，一步完成。</para>
    /// </summary>
    public const uint USBDEVFS_DISCONNECT_CLAIM = 0x8108551B;

    /// <summary>
    /// Disconnect-and-claim flag that skips the request when the interface is already held by the named driver.
    /// <para>断开并申请的标志：接口已被指定名字的驱动持有时跳过该请求。</para>
    /// </summary>
    public const uint USBDEVFS_DISCONNECT_CLAIM_EXCEPT_DRIVER = 0x02;

    /// <summary>
    /// Interface name buffer length in <see cref="Usbdevfs_disconnect_claim"/>; the kernel compares at most this
    /// many bytes.
    /// <para><see cref="Usbdevfs_disconnect_claim"/> 中的接口名缓冲区长度；内核最多比较这么多字节。</para>
    /// </summary>
    public const int USBDEVFS_MAXDRIVERNAME = 255;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Usbdevfs_ioctl
    {
        public int Ifno;
        public int IoctlCode;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Usbdevfs_disconnect_claim
    {
        public uint Interface;
        public uint Flags;
        public fixed byte Driver[USBDEVFS_MAXDRIVERNAME + 1];
    }

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_ioctl arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_disconnect_claim arg);

    /// <summary>
    /// <c>_IOR('U', 4, struct usbdevfs_setinterface)</c>: activates an interface alternate setting through
    /// the kernel instead of a bare control request.
    /// <para><c>_IOR('U', 4, struct usbdevfs_setinterface)</c>：经内核激活接口备用设置，而不是裸控制请求。</para>
    /// </summary>
    public const uint USBDEVFS_SETINTERFACE = 0x80085504;

    /// <summary>
    /// <c>_IOR('U', 5, unsigned int)</c>: activates a device configuration. The kernel rejects it while any
    /// interface of the descriptor is still claimed.
    /// <para><c>_IOR('U', 5, unsigned int)</c>：激活设备配置。描述符仍有接口被申请时内核会拒绝。</para>
    /// </summary>
    public const uint USBDEVFS_SETCONFIGURATION = 0x80045505;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Usbdevfs_setinterface
    {
        public uint Interface;
        public uint Altsetting;
    }

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int ioctl(int Fd, UIntPtr request, ref Usbdevfs_setinterface arg);

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

    /// <summary>
    /// Isochronous URB type code from <c>linux/usbdevice_fs.h</c>.
    /// <para><c>linux/usbdevice_fs.h</c> 中的等时 URB 类型编号。</para>
    /// </summary>
    public const byte USBDEVFS_URB_TYPE_ISO = 0;

    /// <summary>
    /// Interrupt URB type code from <c>linux/usbdevice_fs.h</c>; usbfs uses 1, while
    /// <c>libusb_transfer_type</c> uses 3 for interrupt transfers.
    /// <para><c>linux/usbdevice_fs.h</c> 中的中断 URB 类型编号；usbfs 取 1，
    /// <c>libusb_transfer_type</c> 的中断传输取 3。</para>
    /// </summary>
    public const byte USBDEVFS_URB_TYPE_INTERRUPT = 1;

    /// <summary>
    /// Control URB type code from <c>linux/usbdevice_fs.h</c>.
    /// <para><c>linux/usbdevice_fs.h</c> 中的控制 URB 类型编号。</para>
    /// </summary>
    public const byte USBDEVFS_URB_TYPE_CONTROL = 2;

    /// <summary>
    /// Bulk URB type code from <c>linux/usbdevice_fs.h</c>; usbfs uses 3, while
    /// <c>libusb_transfer_type</c> uses 2 for bulk transfers.
    /// <para><c>linux/usbdevice_fs.h</c> 中的批量 URB 类型编号；usbfs 取 3，
    /// <c>libusb_transfer_type</c> 的批量传输取 2。</para>
    /// </summary>
    public const byte USBDEVFS_URB_TYPE_BULK = 3;

    /// <summary>
    /// usbfs flag that rejects a short bulk or interrupt transfer.
    /// <para>usbfs 标志，表示拒绝短的批量或中断传输。</para>
    /// </summary>
    public const uint USBDEVFS_URB_SHORT_NOT_OK = 0x0001;

    /// <summary>
    /// usbfs flag that schedules an isochronous URB as soon as possible.
    /// <para>usbfs 标志，表示尽快调度等时 URB。</para>
    /// </summary>
    public const uint USBDEVFS_URB_ISO_ASAP = 0x0002;

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