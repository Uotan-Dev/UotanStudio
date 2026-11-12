using LibUsbDotNet.LibUsb;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// Creates the LibUsbDotNet context this backend uses, with finalization suppressed so a disposal failure can never
/// abort the process.
/// <para>创建本后端使用的 LibUsbDotNet 上下文，并抑制终结，使释放失败绝不会中止进程。</para>
/// </summary>
/// <remarks>
/// LibUsbDotNet 3.0.224's <c>UsbContext</c> has a finalizer whose body is <c>Dispose()</c> with no exception guard: it
/// joins the hot-plug thread, closes every open device and ends the native context. An exception escaping a finalizer
/// makes the .NET runtime call <c>abort()</c> on the finalizer thread, which is a process crash with no managed stack.
/// Every context created here is disposed deterministically by its owner (<c>using</c>, <c>LibUsbDevice.Dispose</c> or
/// <c>UsbLibUsbHotplugMonitor.Dispose</c>), so the finalizer only adds that abort path; suppressing it turns a missed
/// disposal into a diagnosable handle leak instead.
/// <para>LibUsbDotNet 3.0.224 的 <c>UsbContext</c> 带终结器，其方法体是无异常保护的 <c>Dispose()</c>：它会 Join 热插拔
/// 线程、关闭所有已打开设备并结束原生上下文。异常逃出终结器会让 .NET 运行时在终结器线程调用 <c>abort()</c>，表现为
/// 没有托管栈的进程崩溃。此处创建的每个上下文都由其属主确定性释放（<c>using</c>、<c>LibUsbDevice.Dispose</c> 或
/// <c>UsbLibUsbHotplugMonitor.Dispose</c>），终结器只会增加这条中止路径；抑制它能把漏掉的释放变成可诊断的句柄泄漏。</para>
/// </remarks>
internal static class LibUsbContextFactory
{
    /// <summary>
    /// Creates an initialized context whose finalizer is suppressed.
    /// <para>创建已初始化且终结被抑制的上下文。</para>
    /// </summary>
    /// <returns>The context the caller owns and must dispose. <para>调用方拥有且必须释放的上下文。</para></returns>
    /// <exception cref="DllNotFoundException">The native libusb runtime cannot be resolved.
    /// <para>无法解析原生 libusb 运行库。</para></exception>
    /// <exception cref="EntryPointNotFoundException">The resolved runtime misses a required entry point.
    /// <para>已解析的运行库缺少必需的入口点。</para></exception>
    /// <exception cref="LibUsbDotNet.LibUsb.UsbException">The native runtime failed to initialize.
    /// <para>原生运行库初始化失败。</para></exception>
    internal static UsbContext Create()
    {
        var context = new UsbContext();
        GC.SuppressFinalize(context);
        return context;
    }
}