using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

/// <summary>
/// SafeHandle wrapper for a Linux usbfs file descriptor; guarantees close() on finalization.
/// <para>Linux usbfs 文件描述符的 SafeHandle 包装；确保终结时调用 close()。</para>
/// </summary>
internal sealed class LinuxUsbFd : SafeHandleZeroOrMinusOneIsInvalid
{
    public LinuxUsbFd()
        : base(ownsHandle: true)
    {
    }

    /// <summary>
    /// Sets the wrapped file descriptor (e.g. from open()).
    /// <para>设置被包装的文件描述符（例如来自 open()）。</para>
    /// </summary>
    internal void SetFd(int fd) => SetHandle(new IntPtr(fd));

    protected override bool ReleaseHandle()
    {
        _ = LinuxUsbAPI.close((int)handle);
        return true;
    }
}