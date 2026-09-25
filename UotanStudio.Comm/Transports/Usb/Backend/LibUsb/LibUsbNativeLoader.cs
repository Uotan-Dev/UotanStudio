using System.Reflection;
using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// Loads the native libusb runtime from a path supplied by the caller, so the
/// libusb backend works on hosts where the bare SONAME <c>libusb-1.0</c> is not
/// on the loader's default search path (e.g. macOS with Homebrew/MacPorts
/// installs, or Windows with a vendored dll).
/// <para>从调用者传入的路径加载原生 libusb 运行时，使 libusb 后端在裸 SONAME
/// <c>libusb-1.0</c> 不在加载器默认搜索路径的主机上也能工作（例如 macOS 上
/// Homebrew/MacPorts 安装，或 Windows 上随附 dll）。</para>
/// </summary>
/// <remarks>
/// <b>Why caller-provided path.</b> LibUsbDotNet 3.0.224
/// registers its own <c>NativeLibrary.SetDllImportResolver</c> and probes
/// for <c>libusb-1.0</c> by bare SONAME, which misses installs that are not
/// on the loader's default search path. This loader takes the path explicitly
/// from the caller and pre-loads the dylib by absolute path BEFORE any LibUsbDotNet
/// type is JIT'd. dyld then resolves the later bare-SONAME <c>dlopen("libusb-1.0")</c>
/// against the already-loaded image, so LibUsbDotNet's own resolver succeeds without
/// a process restart.
/// <para><b>为何采用调用者传入路径。</b>LibUsbDotNet 3.0.224
/// 自注册 <c>NativeLibrary.SetDllImportResolver</c> 并以裸 SONAME
/// <c>libusb-1.0</c> 探测，无法命中不在加载器默认搜索路径上的安装。本加载器由调用者显式
/// 传入路径，并在任何 LibUsbDotNet 类型被 JIT 前按绝对路径预加载 dylib。dyld 随后
/// 以已加载映像解析后续的裸 SONAME <c>dlopen("libusb-1.0")</c>，使 LibUsbDotNet
/// 自身解析器无需重启进程即可成功。</para>
///
/// <b>Usage.</b> Call <c>UsbCommunicationLayer.SetLibusbLibraryPath(path)</c>
/// (or the <c>FirmwareKitComm</c> passthrough) BEFORE the first enumeration or
/// session open. The path is validated to exist and pre-loaded immediately.
/// Without a configured path the backend behaves exactly as before (bare SONAME
/// probe, which works where the runtime is already on the loader's search path).
/// <para><b>用法。</b>在首次枚举或打开会话之前调用
/// <c>UsbCommunicationLayer.SetLibusbLibraryPath(path)</c>（或
/// <c>FirmwareKitComm</c> 透传）。路径会立即校验存在并预加载。未配置路径时后端
/// 行为与之前完全一致（裸 SONAME 探测，在运行时已位于加载器搜索路径时有效）。</para>
///
/// <b>netstandard2.0.</b> <c>NativeLibrary</c> is net5+-only; on
/// netstandard2.0 the loader pre-loads via <c>dlopen</c> / <c>LoadLibraryW</c>
/// P/Invokes. <c>NativeLibrary.SetDllImportResolver</c> (which needs the
/// loaded handle to answer our own <c>[DllImport("libusb-1.0")]</c> probes) is
/// likewise net5+-only; on netstandard2.0 the pre-load alone must make the bare
/// SONAME resolvable, otherwise the backend reports the runtime as unavailable.
/// <para><b>netstandard2.0。</b><c>NativeLibrary</c> 仅 net5+ 可用；
/// netstandard2.0 上加载器经 <c>dlopen</c> / <c>LoadLibraryW</c> P/Invoke 预加载。
/// <c>NativeLibrary.SetDllImportResolver</c>（需用已加载句柄回答我们自身的
/// <c>[DllImport("libusb-1.0")]</c> 探测）同样仅 net5+ 可用；netstandard2.0 上
/// 仅凭预加载须使裸 SONAME 可解析，否则后端报告运行时不可用。</para>
/// </remarks>
internal static partial class LibUsbNativeLoader
{
    private static readonly Lock Gate = new();
    private static string? _libraryPath;
    private static IntPtr _libraryHandle;
#if NET5_0_OR_GREATER
    private static bool _resolverRegistered;
#endif

    /// <summary>
    /// Gets the caller-configured absolute path, or <c>null</c> when none was set.
    /// <para>获取调用者配置的绝对路径；未配置时为 <c>null</c>。</para>
    /// </summary>
    public static string? LibraryPath
    {
        get
        {
            lock (Gate)
            {
                return _libraryPath;
            }
        }
    }

    /// <summary>
    /// Sets the absolute path to the native libusb runtime and pre-loads it so the
    /// bare SONAME resolves. Validates that the file exists; throws
    /// <see cref="FileNotFoundException"/> when it does not.
    /// <para>设置原生 libusb 运行时的绝对路径并预加载，使裸 SONAME 可解析。校验文件
    /// 存在；不存在时抛 <see cref="FileNotFoundException"/>。</para>
    /// </summary>
    /// <param name="libraryPath">Absolute path to <c>libusb-1.0.dylib</c>/<c>.so</c>/<c>.dll</c>.
    /// <para><c>libusb-1.0.dylib</c>/<c>.so</c>/<c>.dll</c> 的绝对路径。</para></param>
    public static void SetLibraryPath(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            throw new ArgumentException("A non-empty libusb library path is required.", nameof(libraryPath));
        }

        var full = Path.GetFullPath(libraryPath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"libusb native runtime not found at '{full}'.", full);
        }

        lock (Gate)
        {
            if (_libraryPath == full && _libraryHandle != IntPtr.Zero)
            {
                return;
            }

            _libraryPath = full;
            _libraryHandle = LoadNative(full);
        }
    }

    private static IntPtr LoadNative(string fullPath)
    {
#if NET5_0_OR_GREATER
        var handle = NativeLibrary.Load(fullPath);
        RegisterResolverOnce();
        return handle;
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Dlopen(fullPath, RtlNowLocal);
        }
        return LoadLibraryW(fullPath);
#endif
    }

#if NET5_0_OR_GREATER
    private static void RegisterResolverOnce()
    {
        if (_resolverRegistered)
        {
            return;
        }

        _resolverRegistered = true;
        NativeLibrary.SetDllImportResolver(typeof(LibUsbNativeLoader).Assembly, ResolveLibrary);
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "libusb-1.0" or "libusb-1.0.dylib" or "libusb-1.0.so" or "libusb-1.0.dll")
        {
            lock (Gate)
            {
                if (_libraryHandle != IntPtr.Zero)
                {
                    return _libraryHandle;
                }

                if (_libraryPath != null)
                {
                    return LoadNative(_libraryPath);
                }
            }
        }

        return IntPtr.Zero;
    }
#endif

#if !NET5_0_OR_GREATER
    private const int RtlNowLocal = 0x2 | 0x1;

    [LibraryImport("libSystem.dylib", EntryPoint = "dlopen")]
    private static partial IntPtr Dlopen([MarshalAs(UnmanagedType.LPStr)] string path, int mode);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW")]
    private static partial IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string path);
#endif
}