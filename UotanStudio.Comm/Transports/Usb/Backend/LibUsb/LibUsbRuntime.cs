namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

/// <summary>
/// The public entry point that lets a host register an explicit native library path for the libusb backend.
/// The repository ships the binaries for the supported RIDs and the build copies them next to the application,
/// so registration is only needed for a RID without a bundled file (for example a system-installed libusb);
/// opening the device handle itself stays in the Comm transport layer.
/// <para>公开入口，供宿主为 libusb 后端注册显式的原生库路径。仓库已为受支持的 RID 附带二进制，构建时会复制到
/// 程序目录，因此只有缺少对应 RID 文件时（例如使用系统安装的 libusb）才需要注册；直接打开设备句柄的仍是
/// Comm 的传输层。</para>
/// </summary>
/// <remarks>
/// The native binary has no single managed loader, so the host must not guess the file name or the platform
/// subdirectory: it registers exactly the absolute path it resolved and verified.
/// <para>原生库没有统一的托管加载器，因此宿主不应猜测文件名或平台子目录：它只注册自己解析并校验过的绝对路径。</para>
/// </remarks>
public static class LibUsbRuntime
{
    /// <summary>
    /// Gets the absolute path of the native library the host registered; <see langword="null"/> when none was
    /// registered.
    /// <para>获取调用者注册的原生库绝对路径；未注册时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The configured absolute path, or <see langword="null"/>.
    /// <para>已配置的绝对路径；未配置时为 <see langword="null"/>。</para></value>
    public static string? LibraryPath => LibUsbNativeLoader.LibraryPath;

    /// <summary>Registers the absolute path of the native libusb runtime and pre-loads it so the bare SONAME resolves, taking precedence over the file next to the application. Throws <see cref="FileNotFoundException"/> when the file does not exist. Registering after a failed availability probe re-enables the backend; switching the runtime after a successful native call is not supported because resolved imports keep their original image.
    /// <para>注册原生 libusb 运行时的绝对路径并预加载，使裸 SONAME 可解析，并优先于程序目录中的文件。文件不存在时抛 <see cref="FileNotFoundException"/>。探测失败之后再注册会重新启用后端；已经成功调用原生接口之后再切换运行库不受支持，因为已解析的导入仍指向原映像。</para></summary>
    /// <param name="libraryPath">Absolute path to <c>libusb-1.0.dylib</c>, <c>libusb-1.0.so</c> or <c>libusb-1.0.dll</c>.
    /// <para><c>libusb-1.0.dylib</c>、<c>libusb-1.0.so</c> 或 <c>libusb-1.0.dll</c> 的绝对路径。</para></param>
    /// <exception cref="ArgumentException"><paramref name="libraryPath"/> is blank.
    /// <para><paramref name="libraryPath"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist.<para>文件不存在。</para></exception>
    public static void SetLibraryPath(string libraryPath)
        => LibUsbNativeLoader.SetLibraryPath(libraryPath);
}