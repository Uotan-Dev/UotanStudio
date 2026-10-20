using System.Reflection;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// Loads the native libusb runtime from the path the caller registered or, when none was
/// registered, from the platform library file copied next to the application, so the libusb
/// backend works where the bare SONAME <c>libusb-1.0</c> is not on the loader's default
/// search path.
/// <para>从调用者注册的路径加载原生 libusb 运行时；未注册时使用复制到程序目录的平台库文件，
/// 使 libusb 后端在裸 SONAME <c>libusb-1.0</c> 不在加载器默认搜索路径时也能工作。</para>
/// </summary>
/// <remarks>
/// <b>Why an explicit path.</b> LibUsbDotNet 3.0.224
/// registers its own <c>NativeLibrary.SetDllImportResolver</c> and probes
/// for <c>libusb-1.0</c> by bare SONAME, which misses installs that are not
/// on the loader's default search path. This loader resolves the effective path
/// (caller-registered first, then the application directory) and pre-loads the
/// library by absolute path BEFORE any LibUsbDotNet type is JIT'd. The platform
/// loader then resolves the later bare-SONAME <c>dlopen("libusb-1.0")</c>
/// against the already-loaded image, so LibUsbDotNet's own resolver succeeds without
/// a process restart.
/// <para><b>为何需要显式路径。</b>LibUsbDotNet 3.0.224
/// 自注册 <c>NativeLibrary.SetDllImportResolver</c> 并以裸 SONAME
/// <c>libusb-1.0</c> 探测，无法命中不在加载器默认搜索路径上的安装。本加载器解析有效路径
/// （调用者注册优先，其次程序目录），并在任何 LibUsbDotNet 类型被 JIT 前按绝对路径预加载库。
/// 平台加载器随后以已加载映像解析后续的裸 SONAME <c>dlopen("libusb-1.0")</c>，
/// 使 LibUsbDotNet 自身解析器无需重启进程即可成功。</para>
///
/// <b>Usage.</b> The backend reports itself available only when
/// <see cref="TryEnsureLoaded"/> can load the caller-registered path or the platform file
/// copied next to the application (<c>libusb-1.0.dll</c>, <c>libusb-1.0.dylib</c>,
/// <c>libusb-1.0.so</c> or <c>libusb-1.0.so.0</c>); a libusb installed elsewhere on the
/// system does not enable it. Register an explicit path with
/// <c>UsbCommunicationLayer.SetLibusbLibraryPath(path)</c> (or the <c>CommKit</c>
/// passthrough) before the first enumeration or session open; the path is validated to exist
/// and pre-loaded immediately, and registering after a failed probe re-enables the backend.
/// Switching the runtime after a successful native call is not supported because already
/// resolved imports keep their original image.
/// <para><b>用法。</b>只有当 <see cref="TryEnsureLoaded"/> 能加载调用者注册的路径或复制到
/// 程序目录的平台文件（<c>libusb-1.0.dll</c>、<c>libusb-1.0.dylib</c>、
/// <c>libusb-1.0.so</c> 或 <c>libusb-1.0.so.0</c>）时，后端才报告可用；仅安装在系统
/// 其他位置的 libusb 不会启用它。可在首次枚举或打开会话之前用
/// <c>UsbCommunicationLayer.SetLibusbLibraryPath(path)</c>（或 <c>CommKit</c> 透传）
/// 注册显式路径；路径会立即校验存在并预加载，探测失败之后再注册会重新启用后端。
/// 已经成功调用原生接口之后再切换运行库不受支持，因为已解析的导入仍指向原映像。</para>
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

    private static readonly string[] PlatformLibraryFileNames =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["libusb-1.0.dll"]
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ["libusb-1.0.dylib"]
                : ["libusb-1.0.so", "libusb-1.0.so.0"];

    private static string? _libraryPath;
    private static string? _resolvedPath;
    private static IntPtr _libraryHandle;
    private static int _revision;
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
    /// Gets a monotonic revision that changes whenever the effective native library changes, so
    /// availability probes can be cached and invalidated after <see cref="SetLibraryPath"/>.
    /// <para>获取随着有效原生库变化而递增的修订号，使可用性探测可以缓存，并在
    /// <see cref="SetLibraryPath"/> 之后失效。</para>
    /// </summary>
    /// <value>The current revision; start value is zero. <para>当前修订号；起始值为零。</para></value>
    public static int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// Sets the absolute path to the native libusb runtime and pre-loads it so the
    /// bare SONAME resolves, taking precedence over the file next to the application.
    /// Validates that the file exists; throws <see cref="FileNotFoundException"/> when it
    /// does not. A new path advances <see cref="Revision"/> so availability probes re-run.
    /// <para>设置原生 libusb 运行时的绝对路径并预加载，使裸 SONAME 可解析，并优先于程序目录中的
    /// 文件。校验文件存在；不存在时抛 <see cref="FileNotFoundException"/>。新路径会递增
    /// <see cref="Revision"/>，使可用性探测重新执行。</para>
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
            _resolvedPath = full;
            _libraryHandle = LoadNative(full);
            Interlocked.Increment(ref _revision);
        }
    }

    /// <summary>
    /// Resolves and pre-loads the native runtime: the caller-configured path wins, otherwise the
    /// platform library file next to the application is used. Returns <c>false</c> when neither
    /// exists, so a system-installed libusb that was never bundled or registered does not count as
    /// an available runtime.
    /// <para>解析并预加载原生运行时：调用者配置的路径优先，否则使用程序目录中的平台库文件。
    /// 两者都不存在时返回 <c>false</c>，因此仅安装在系统路径、既未随包复制也未显式注册的
    /// libusb 不会被计为可用运行时。</para>
    /// </summary>
    /// <param name="libraryPath">The loaded absolute path on success. <para>成功时为已加载的绝对路径。</para></param>
    /// <returns><c>true</c> when a runtime is loaded or was already loaded.
    /// <para>运行时已加载或此前已加载时为 <c>true</c>。</para></returns>
    public static bool TryEnsureLoaded(out string? libraryPath)
    {
        lock (Gate)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                libraryPath = _resolvedPath;
                return true;
            }

            var candidate = _libraryPath ?? FindProgramDirectoryLibrary();
            if (candidate is null)
            {
                libraryPath = null;
                return false;
            }

            try
            {
                _libraryHandle = LoadNative(candidate);
            }
            catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
            {
                UsbTrace.Log($"LibUsbNativeLoader: native library load failed for '{candidate}': {ex.GetType().Name}: {ex.Message}");
                libraryPath = null;
                return false;
            }

            _resolvedPath = candidate;
            Interlocked.Increment(ref _revision);
            libraryPath = candidate;
            return true;
        }
    }

    /// <summary>
    /// Finds the platform libusb file copied next to the application by the project's RID
    /// selection, or <c>null</c> when it is absent.
    /// <para>查找项目按 RID 复制到程序目录的平台 libusb 文件；不存在时返回 <c>null</c>。</para>
    /// </summary>
    /// <returns>The absolute candidate path, or <c>null</c>. <para>候选绝对路径；不存在时为 <c>null</c>。</para></returns>
    private static string? FindProgramDirectoryLibrary()
    {
        var directory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        foreach (var fileName in PlatformLibraryFileNames)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
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