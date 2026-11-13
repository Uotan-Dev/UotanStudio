using System.Runtime.InteropServices;
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
/// <b>Allocation gate.</b> A failed constructor does not cancel finalization: when <c>new UsbContext()</c> throws (its
/// first statement binds the <c>libusb-1.0</c> import and calls <c>libusb_init</c>, and the constructor stops at
/// <c>NativeMethods.Init().ThrowOnError()</c>), <see cref="GC.SuppressFinalize"/> is never reached, the runtime still
/// finalizes the half-built object, and <c>Dispose(false)</c> dereferences the never-assigned <c>OpenDevices</c>
/// list — a guaranteed finalizer <see cref="NullReferenceException"/> and process abort. Every precondition of the
/// constructor is therefore verified here <i>before</i> the allocation: the native runtime must load, LibUsbDotNet's
/// own import probing of the application directory must bind a usable image, and <c>libusb_init</c> must succeed on
/// this process. All three gates throw the exception types the constructor itself would have thrown
/// (<see cref="DllNotFoundException"/>, <see cref="UsbException"/>), so existing catch sites keep their behavior.
/// <para><b>分配门禁。</b>构造失败不会取消终结：当 <c>new UsbContext()</c> 抛异常（它的第一条语句绑定
/// <c>libusb-1.0</c> 导入并调用 <c>libusb_init</c>，构造停在 <c>NativeMethods.Init().ThrowOnError()</c>）时，
/// <see cref="GC.SuppressFinalize"/> 永远不会执行，运行时仍会终结该半构造对象，而 <c>Dispose(false)</c> 会解引用
/// 从未赋值的 <c>OpenDevices</c> 列表——必然在终结器线程抛出 <see cref="NullReferenceException"/> 并中止进程。
/// 因此构造函数的全部前置条件都在分配之前在此验证：原生运行库必须可加载，LibUsbDotNet 自身对程序目录的导入
/// 探测必须绑定到可用映像，且 <c>libusb_init</c> 必须在本进程内成功。三道门抛出的都是构造函数本会抛出的异常
/// 类型（<see cref="DllNotFoundException"/>、<see cref="UsbException"/>），既有捕获点的行为不变。</para>
/// </remarks>
internal static partial class LibUsbContextFactory
{
    /// <summary>
    /// Creates an initialized context whose finalizer is suppressed.
    /// <para>创建已初始化且终结被抑制的上下文。</para>
    /// </summary>
    /// <returns>The context the caller owns and must dispose. <para>调用方拥有且必须释放的上下文。</para></returns>
    /// <exception cref="DllNotFoundException">The native libusb runtime cannot be resolved — neither the caller
    /// registered path nor the application-adjacent platform file loads, or LibUsbDotNet's own import probing has no
    /// usable image to bind.
    /// <para>无法解析原生 libusb 运行库——调用者注册的路径与程序目录中的平台文件都无法加载，或 LibUsbDotNet
    /// 自身的导入探测没有可用映像可绑定。</para></exception>
    /// <exception cref="LibUsbDotNet.LibUsb.UsbException">The native runtime failed to initialize.
    /// <para>原生运行库初始化失败。</para></exception>
    internal static UsbContext Create()
    {
        // Gate 1: resolve and preload the caller-registered or application-adjacent runtime.
        if (!LibUsbNativeLoader.TryEnsureLoaded(out _))
        {
            throw new DllNotFoundException(
                "The native libusb runtime could not be loaded from the caller-registered path or the platform file " +
                "next to the application (libusb-1.0.dylib, libusb-1.0.0.dylib, libusb-1.0.dll, libusb-1.0.so, " +
                "libusb-1.0.so.0); place a compatible libusb next to the application or register its absolute path " +
                "with UsbCommunicationLayer.SetLibusbLibraryPath (or CommKit.SetLibusbLibraryPath).");
        }

        // Gate 2: LibUsbDotNet resolves its own import through its resolver and then the runtime's default probing
        // of the application directory; the loader's preloaded image only answers this assembly's imports. When the
        // application directory has no loadable platform file, its constructor would throw after allocation.
        if (!LibUsbNativeLoader.TryLoadApplicationDirectoryLibrary())
        {
            throw new DllNotFoundException(
                "LibUsbDotNet resolves its own libusb import from the application directory, which contains no " +
                "loadable platform file (libusb-1.0.dylib, libusb-1.0.0.dylib, libusb-1.0.dll, libusb-1.0.so, " +
                "libusb-1.0.so.0); a registered external path serves only this assembly's imports. Place a " +
                "compatible libusb next to the application.");
        }

        // Gate 3: libusb_init must succeed on the preloaded image; otherwise the constructor's ThrowOnError would
        // throw after allocation. libusb contexts are independent, so the probe's init/exit does not affect the
        // context allocated below.
        var result = libusb_init(out var probeContext);
        if (result == 0 && probeContext != IntPtr.Zero)
        {
            libusb_exit(probeContext);
        }
        else
        {
            throw new UsbException((LibUsbDotNet.Error)result);
        }

        var context = new UsbContext();
        GC.SuppressFinalize(context);
        return context;
    }

    [LibraryImport("libusb-1.0")]
    private static partial int libusb_init(out IntPtr context);

    [LibraryImport("libusb-1.0")]
    private static partial void libusb_exit(IntPtr context);
}