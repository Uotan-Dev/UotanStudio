using Microsoft.Win32.SafeHandles;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// SafeHandle for a WinUSB interface handle; guarantees WinUsb_Free on finalization.
/// <para>WinUSB 接口句柄的 SafeHandle 包装；确保终结时调用 WinUsb_Free。</para>
/// </summary>
internal sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeWinUsbHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        _ = WinUSBAPI.WinUsb_Free(handle);
        return true;
    }
}